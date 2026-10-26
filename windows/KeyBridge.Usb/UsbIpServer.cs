using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KeyBridge.Usb;

/// <summary>A USB/IP 1.1.1 export, restricted to IPv4 loopback. A signed VHCI client is required to enumerate it.</summary>
public sealed class UsbIpServer : IAsyncDisposable
{
    private readonly ICtapTransport _transport;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<long, Task> _clients = new();
    private readonly SemaphoreSlim _connectionSlots = new(8, 8);
    private Task? _run;
    private int _imported;
    private long _clientId;
    public event Action<string>? StatusChanged;
    public int BoundPort => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public bool IsImported => Volatile.Read(ref _imported) == 1;

    public UsbIpServer(ICtapTransport transport, int port = 3240)
    {
        ArgumentNullException.ThrowIfNull(transport);
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _transport = transport;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Server.ExclusiveAddressUse = true;
    }

    /// <summary>Starts immediately and completes when cancelled. Run as a background task; bind errors propagate.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_run is not null) throw new InvalidOperationException("USB/IP server is already started.");
        _listener.Start(8);
        StatusChanged?.Invoke($"USB/IP listening on 127.0.0.1:{BoundPort}; Windows enumeration is not yet verified.");
        return _run = RunAsync(cancellationToken);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(linked.Token).ConfigureAwait(false);
                if (!_connectionSlots.Wait(0)) { client.Dispose(); continue; }
                client.NoDelay = true;
                var id = Interlocked.Increment(ref _clientId);
                var task = HandleClientAsync(client, linked.Token);
                _clients[id] = task;
                _ = task.ContinueWith(_ => { _clients.TryRemove(id, out var ignored); _connectionSlots.Release(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (SocketException) when (linked.IsCancellationRequested) { }
        finally
        {
            _listener.Stop();
            await linked.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(_clients.Values).ConfigureAwait(false);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            bool ownsImport = false;
            try
            {
                var stream = client.GetStream();
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                handshake.CancelAfter(TimeSpan.FromSeconds(10));
                var header = new byte[8];
                await stream.ReadExactlyAsync(header, handshake.Token).ConfigureAwait(false);
                if (BinaryPrimitives.ReadUInt16BigEndian(header) != 0x0111 || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)) != 0)
                    return;
                var command = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2));
                if (command == 0x8005)
                {
                    var reply = new byte[328];
                    ControlHeader(reply, 5, 0);
                    BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(8), 1);
                    UsbDescriptors.UsbIpDeviceRecord().CopyTo(reply, 12);
                    reply[324] = 3; // interface class, subclass, protocol, padding
                    await stream.WriteAsync(reply, handshake.Token).ConfigureAwait(false);
                    return;
                }
                if (command != 0x8003) return;
                var busBytes = new byte[32];
                await stream.ReadExactlyAsync(busBytes, handshake.Token).ConfigureAwait(false);
                var terminator = Array.IndexOf(busBytes, (byte)0);
                var bus = terminator < 0 ? "" : Encoding.ASCII.GetString(busBytes, 0, terminator);
                uint status = 0;
                if (bus != UsbDescriptors.BusId || busBytes.AsSpan(terminator + 1).IndexOfAnyExcept((byte)0) >= 0) status = 1;
                else if (Interlocked.CompareExchange(ref _imported, 1, 0) != 0) status = 2;
                else ownsImport = true;
                var response = new byte[status == 0 ? 320 : 8];
                ControlHeader(response, 3, status);
                if (status == 0) UsbDescriptors.UsbIpDeviceRecord().CopyTo(response, 8);
                await stream.WriteAsync(response, handshake.Token).ConfigureAwait(false);
                if (status != 0) return;
                StatusChanged?.Invoke("VHCI client imported the USB/IP device; verify HID enumeration with Windows diagnostics.");
                await using var device = new CtapHidDevice(_transport);
                await using var session = new Session(stream, device, cancellationToken);
                await session.RunAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException) { }
            finally
            {
                if (ownsImport)
                {
                    Interlocked.Exchange(ref _imported, 0);
                    StatusChanged?.Invoke("Virtual USB client detached.");
                }
            }
        }
    }

    private static void ControlHeader(Span<byte> buffer, ushort reply, uint status)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buffer, 0x0111);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[2..], reply);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], status);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        if (_run is not null) await _run.ConfigureAwait(false);
        _stop.Dispose();
        _connectionSlots.Dispose();
    }

    private sealed class Session : IAsyncDisposable
    {
        private readonly NetworkStream _stream;
        private readonly CtapHidDevice _hid;
        private readonly UsbControlEndpoint _control;
        private readonly CancellationTokenSource _lifetime;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly object _inputLock = new();
        private readonly LinkedList<PendingInput> _inputs = new();
        private readonly Dictionary<uint, LinkedListNode<PendingInput>> _pending = [];
        private TaskCompletionSource _inputReady = NewSignal();
        private Task? _pump;

        public Session(NetworkStream stream, CtapHidDevice hid, CancellationToken cancellationToken)
        {
            _stream = stream; _hid = hid; _control = new(hid);
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, hid.LifetimeToken);
        }

        public async Task RunAsync()
        {
            _pump = PumpInputsAsync();
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var header = new byte[48];
                    await _stream.ReadExactlyAsync(header, _lifetime.Token).ConfigureAwait(false);
                    var command = U32(header, 0);
                    var sequence = U32(header, 4);
                    var direction = U32(header, 12);
                    var endpoint = U32(header, 16);
                    if (command == 2)
                    {
                        var target = U32(header, 20);
                        bool removed;
                        lock (_inputLock)
                        {
                            removed = _pending.Remove(target, out var node);
                            if (removed) _inputs.Remove(node!);
                        }
                        // A successful unlink suppresses that URB's RET_SUBMIT entirely.
                        await ReplyAsync(header, 4, removed ? -104 : 0, [], 0).ConfigureAwait(false);
                        continue;
                    }
                    if (command != 1) throw new InvalidDataException("Unsupported USB/IP command.");
                    var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(24));
                    var packets = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(32));
                    if (length is < 0 or > 65536 || direction > 1 || endpoint > 15 || packets is not (0 or -1))
                        throw new InvalidDataException("Unsupported or oversized USB/IP transfer.");
                    byte[] data = new byte[direction == 0 ? length : 0];
                    if (data.Length != 0) await _stream.ReadExactlyAsync(data, _lifetime.Token).ConfigureAwait(false);
                    if (U32(header, 8) != UsbDescriptors.DeviceId)
                    { await ReplyAsync(header, 3, -19, [], 0).ConfigureAwait(false); continue; }
                    if (endpoint == 0)
                    {
                        var setup = header.AsSpan(40, 8).ToArray();
                        if (BinaryPrimitives.ReadUInt16LittleEndian(setup.AsSpan(6)) != length)
                        { await ReplyAsync(header, 3, -22, [], 0).ConfigureAwait(false); continue; }
                        var result = await _control.TransferAsync(setup, data, direction, _lifetime.Token).ConfigureAwait(false);
                        await ReplyAsync(header, 3, result.Status, direction == 1 ? result.Data : [],
                            result.Status == 0 ? direction == 1 ? result.Data.Length : length : 0).ConfigureAwait(false);
                    }
                    else if (!_control.Configured)
                        await ReplyAsync(header, 3, -32, [], 0).ConfigureAwait(false);
                    else if (endpoint == 1 && direction == 0 && length == 64)
                    {
                        await _hid.SendReportAsync(data, _lifetime.Token).ConfigureAwait(false);
                        await ReplyAsync(header, 3, 0, [], 64).ConfigureAwait(false);
                    }
                    else if (endpoint == 1 && direction == 1 && length >= 64)
                    {
                        bool added;
                        lock (_inputLock)
                        {
                            added = _pending.Count < 64 && !_pending.ContainsKey(sequence);
                            if (added)
                            {
                                _pending.Add(sequence, _inputs.AddLast(new PendingInput(header)));
                                _inputReady.TrySetResult();
                            }
                        }
                        if (!added) await ReplyAsync(header, 3, -16, [], 0).ConfigureAwait(false);
                    }
                    else await ReplyAsync(header, 3, -32, [], 0).ConfigureAwait(false);
                }
            }
            finally { await _lifetime.CancelAsync().ConfigureAwait(false); }
        }

        private async Task PumpInputsAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var report = await _hid.ReadReportAsync(_lifetime.Token).ConfigureAwait(false);
                    PendingInput? input = null;
                    while (input is null)
                    {
                        Task ready;
                        lock (_inputLock)
                        {
                            if (_inputs.First is { } node)
                            {
                                input = node.Value;
                                _inputs.RemoveFirst();
                                _pending.Remove(U32(input.Header, 4));
                            }
                            if (_inputs.Count == 0 && _inputReady.Task.IsCompleted) _inputReady = NewSignal();
                            ready = _inputReady.Task;
                        }
                        if (input is null) await ready.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                    }
                    await ReplyAsync(input.Header, 3, 0, report, report.Length).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (IOException) { await _lifetime.CancelAsync().ConfigureAwait(false); }
        }

        private async Task ReplyAsync(byte[] request, uint command, int status, byte[] data, int actualLength)
        {
            var response = new byte[48 + data.Length];
            BinaryPrimitives.WriteUInt32BigEndian(response, command);
            request.AsSpan(4, 4).CopyTo(response.AsSpan(4));
            // RET headers set devid/direction/endpoint to zero as required by USB/IP.
            BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(20), status);
            if (command == 3)
            {
                BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(24), actualLength);
                BinaryPrimitives.WriteInt32BigEndian(response.AsSpan(32), 0); // no isochronous packets
            }
            data.CopyTo(response, 48);
            await _writeLock.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try { await _stream.WriteAsync(response, _lifetime.Token).ConfigureAwait(false); }
            finally { _writeLock.Release(); }
        }

        private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        private sealed record PendingInput(byte[] Header);

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            if (_pump is not null) await _pump.ConfigureAwait(false);
            _lifetime.Dispose(); _writeLock.Dispose();
        }
    }
}
