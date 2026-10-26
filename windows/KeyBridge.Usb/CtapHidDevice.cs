using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace KeyBridge.Usb;

/// <summary>One physical-authenticator transaction at a time, independently of USB/IP.</summary>
public sealed class CtapHidDevice : IAsyncDisposable
{
    private readonly ICtapTransport _transport;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<byte[]> _reports = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
    {
        FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false
    });
    private readonly HashSet<uint> _channels = [];
    private readonly TimeSpan _assemblyTimeout;
    private readonly TimeSpan _transactionTimeout;
    private readonly Task _watchdog;
    private Assembly? _assembly;
    private Operation? _active;
    private readonly HashSet<Task> _operations = [];
    internal CancellationToken LifetimeToken => _lifetime.Token;

    public CtapHidDevice(ICtapTransport transport, TimeSpan? assemblyTimeout = null, TimeSpan? transactionTimeout = null)
    {
        _transport = transport;
        _assemblyTimeout = assemblyTimeout ?? TimeSpan.FromSeconds(3);
        _transactionTimeout = transactionTimeout ?? TimeSpan.FromSeconds(130);
        _watchdog = WatchdogAsync();
    }

    public async ValueTask<byte[]> ReadReportAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        return await _reports.Reader.ReadAsync(linked.Token).ConfigureAwait(false);
    }

    public async Task SendReportAsync(ReadOnlyMemory<byte> report, CancellationToken cancellationToken = default)
    {
        if (report.Length != HidFraming.ReportSize) throw new ArgumentException("HID reports must contain exactly 64 bytes.", nameof(report));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        List<byte[]> output = [];
        Operation? operation = null;
        try
        {
            ProcessReport(report.Span, output, out operation);
        }
        finally { _gate.Release(); }
        foreach (var packet in output) Enqueue(packet);
        if (operation is not null)
        {
            var task = RunOperationAsync(operation);
            lock (_operations) _operations.Add(task);
            _ = task.ContinueWith(completed => { lock (_operations) _operations.Remove(completed); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private void ProcessReport(ReadOnlySpan<byte> packet, List<byte[]> output, out Operation? operation)
    {
        operation = null;
        var channel = BinaryPrimitives.ReadUInt32BigEndian(packet);
        if (channel == 0) { Error(channel, CtapHidError.InvalidChannel, output); return; }
        var initial = (packet[4] & 0x80) != 0;
        var command = (CtapHidCommand)(packet[4] & 0x7f);

        // INIT is the mandated resynchronization primitive, even during an unfinished transaction.
        if (initial && command == CtapHidCommand.Init)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(packet[5..]);
            if (length != 8) { Error(channel, CtapHidError.InvalidLength, output); return; }
            if (channel != HidFraming.BroadcastChannel && !_channels.Contains(channel))
            { Error(channel, CtapHidError.InvalidChannel, output); return; }
            uint assigned;
            if (channel == HidFraming.BroadcastChannel)
            {
                if (_channels.Count >= 32) { Error(channel, CtapHidError.ChannelBusy, output); return; }
                do { assigned = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4)); }
                while (assigned is 0 or HidFraming.BroadcastChannel || !_channels.Add(assigned));
            }
            else
            {
                assigned = channel;
                if (_assembly?.Channel == channel) _assembly = null;
                if (_active?.Channel == channel)
                {
                    _active.SuppressResponse = true;
                    _active.Cancellation.Cancel();
                    _active = null;
                }
            }
            var response = new byte[17];
            packet.Slice(7, 8).CopyTo(response);
            BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(8), assigned);
            response[12] = 2; // CTAPHID protocol version
            response[13] = 0; response[14] = 1; response[15] = 0;
            response[16] = 0x0c; // CBOR and NMSG; no unsupported U2F, WINK or LOCK capability.
            output.AddRange(HidFraming.Fragment(channel, CtapHidCommand.Init, response));
            return;
        }
        if (channel == HidFraming.BroadcastChannel || !_channels.Contains(channel))
        { Error(channel, CtapHidError.InvalidChannel, output); return; }

        if (initial && command == CtapHidCommand.Cancel)
        {
            if (BinaryPrimitives.ReadUInt16BigEndian(packet[5..]) != 0)
            { Error(channel, CtapHidError.InvalidLength, output); return; }
            if (_active?.Channel == channel) { _active.CancelledByHost = true; _active.Cancellation.Cancel(); }
            if (_assembly?.Channel == channel) _assembly = null;
            return; // CANCEL has no standalone response.
        }
        if (_active is not null)
        { Error(channel, CtapHidError.ChannelBusy, output); return; }

        if (initial)
        {
            if (_assembly is not null)
            {
                if (_assembly.Channel != channel) { Error(channel, CtapHidError.ChannelBusy, output); return; }
                _assembly = null;
                Error(channel, CtapHidError.InvalidSequence, output);
                return;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(packet[5..]);
            if (length > HidFraming.MaximumMessageSize)
            { Error(channel, CtapHidError.InvalidLength, output); return; }
            if (command is not (CtapHidCommand.Cbor or CtapHidCommand.Ping))
            { Error(channel, CtapHidError.InvalidCommand, output); return; }
            if (command == CtapHidCommand.Cbor && length == 0)
            { Error(channel, CtapHidError.InvalidLength, output); return; }
            var message = new Assembly(channel, command, length);
            var count = Math.Min(57, (int)length);
            packet.Slice(7, count).CopyTo(message.Bytes);
            message.Offset = count;
            _assembly = message;
        }
        else
        {
            if (_assembly is null) { Error(channel, CtapHidError.InvalidSequence, output); return; }
            if (_assembly.Channel != channel) { Error(channel, CtapHidError.ChannelBusy, output); return; }
            if (packet[4] != _assembly.Sequence)
            { _assembly = null; Error(channel, CtapHidError.InvalidSequence, output); return; }
            _assembly.Sequence++;
            var count = Math.Min(59, _assembly.Bytes.Length - _assembly.Offset);
            packet.Slice(5, count).CopyTo(_assembly.Bytes.AsSpan(_assembly.Offset));
            _assembly.Offset += count;
            _assembly.LastPacket = DateTimeOffset.UtcNow;
        }

        if (_assembly.Offset != _assembly.Bytes.Length) return;
        var completed = _assembly;
        _assembly = null;
        if (completed.Command == CtapHidCommand.Ping)
            output.AddRange(HidFraming.Fragment(channel, CtapHidCommand.Ping, completed.Bytes));
        else
        {
            operation = new Operation(channel, completed.Bytes, _lifetime.Token, _transactionTimeout);
            _active = operation;
        }
    }

    private async Task RunOperationAsync(Operation operation)
    {
        byte[] response;
        using var keepaliveStop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var keepalive = KeepaliveAsync(operation, keepaliveStop.Token);
        try
        {
            response = await _transport.ExchangeAsync(operation.Request, operation.Cancellation.Token)
                .WaitAsync(operation.Cancellation.Token).ConfigureAwait(false);
            if (response.Length is 0 or > HidFraming.MaximumMessageSize) response = [0x7f];
        }
        catch (OperationCanceledException) { response = [operation.CancelledByHost ? (byte)0x2d : (byte)0x3a]; }
        catch { response = [0x7f]; } // CTAP2_ERR_OTHER; no invented authenticator success.
        finally
        {
            await keepaliveStop.CancelAsync().ConfigureAwait(false);
            try { await keepalive.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (IOException) { }
        }

        try
        {
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                // A resynchronizing INIT suppresses all output from the old transaction.
                if (ReferenceEquals(_active, operation) && !operation.SuppressResponse)
                {
                    foreach (var packet in HidFraming.Fragment(operation.Channel, CtapHidCommand.Cbor, response))
                        Enqueue(packet);
                    _active = null;
                }
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally { operation.Cancellation.Dispose(); }
    }

    private async Task KeepaliveAsync(Operation operation, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_active, operation) || operation.SuppressResponse) return;
                // The opaque transport does not know whether Android is prompting; PROCESSING is truthful.
                foreach (var packet in HidFraming.Fragment(operation.Channel, CtapHidCommand.KeepAlive, [1]))
                    Enqueue(packet);
            }
            finally { _gate.Release(); }
        }
    }

    private async Task WatchdogAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    if (_assembly is not null && DateTimeOffset.UtcNow - _assembly.LastPacket >= _assemblyTimeout)
                    {
                        var channel = _assembly.Channel;
                        _assembly = null;
                        foreach (var packet in HidFraming.Fragment(channel, CtapHidCommand.Error, [(byte)CtapHidError.MessageTimeout]))
                            Enqueue(packet);
                    }
                }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private void Enqueue(byte[] report)
    {
        if (_reports.Writer.TryWrite(report)) return;
        // Backpressure must never block the command reader that receives the host's next IN URB.
        // A peer that stops draining input is disconnected instead of growing memory or deadlocking.
        _lifetime.Cancel();
        throw new IOException("The HID input queue overflowed; the virtual USB session was closed.");
    }

    private static void Error(uint channel, CtapHidError error, List<byte[]> output) =>
        output.AddRange(HidFraming.Fragment(channel, CtapHidCommand.Error, [(byte)error]));

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _watchdog.ConfigureAwait(false);
        Task[] pending;
        lock (_operations) pending = [.. _operations];
        await Task.WhenAll(pending).ConfigureAwait(false);
        _reports.Writer.TryComplete();
        _lifetime.Dispose();
        _gate.Dispose();
    }

    private sealed class Assembly(uint channel, CtapHidCommand command, int length)
    {
        public uint Channel { get; } = channel;
        public CtapHidCommand Command { get; } = command;
        public byte[] Bytes { get; } = new byte[length];
        public int Offset { get; set; }
        public byte Sequence { get; set; }
        public DateTimeOffset LastPacket { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class Operation(uint channel, byte[] request, CancellationToken lifetime, TimeSpan timeout)
    {
        public uint Channel { get; } = channel;
        public byte[] Request { get; } = request;
        public CancellationTokenSource Cancellation { get; } = CreateCancellation(lifetime, timeout);
        public bool SuppressResponse { get; set; }
        public bool CancelledByHost { get; set; }
        private static CancellationTokenSource CreateCancellation(CancellationToken lifetime, TimeSpan timeout)
        {
            var result = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            result.CancelAfter(timeout);
            return result;
        }
    }
}
