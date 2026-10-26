using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KeyBridge.Usb;

var tests = new (string Name, Func<Task> Run)[]
{
    ("USB descriptors identify a full-speed FIDO HID interface", Descriptors),
    ("CTAPHID INIT allocates independent channels and advertises only implemented capabilities", Init),
    ("PING fragmentation round trips every boundary through 7609 bytes", FragmentBoundaries),
    ("Framing refuses an oversized outbound message", OversizedFragment),
    ("Invalid channels and broadcasts fail explicitly", InvalidChannels),
    ("Empty CBOR, oversized input, and legacy MSG fail explicitly", InvalidCommands),
    ("Out-of-order continuation resets assembly", InvalidSequence),
    ("Reassembly timeout releases the authenticator", AssemblyTimeout),
    ("Concurrent channels receive CHANNEL_BUSY", ConcurrentChannels),
    ("CANCEL reaches the transport and returns KEEPALIVE_CANCEL", Cancellation),
    ("Slow Android requests emit processing keepalives", Keepalive),
    ("Transaction timeout returns ACTION_TIMEOUT", TransactionTimeout),
    ("INIT resynchronization suppresses old authentication output", Resynchronization),
    ("Transport faults cannot produce authentication success", TransportFault),
    ("An undrained HID input queue fails without blocking the OUT reader", OutputBackpressure),
    ("USB/IP DEVLIST uses exact network-order records", Devlist),
    ("USB/IP handles fragmented management and URB headers", ChunkedRequests),
    ("USB/IP IMPORT is exclusive and validates bus identity", ExclusiveImport),
    ("USB/IP control requests enumerate descriptors and configuration", Enumeration),
    ("USB/IP interrupt URBs exchange CTAPHID messages", InterruptRoundTrip),
    ("USB/IP UNLINK suppresses the pending RET_SUBMIT", Unlink),
    ("USB/IP closes an imported session if the host never drains HID input", NetworkBackpressure),
    ("USB/IP invalid transfer sizes close the connection", OversizedUrb),
    ("USB/IP server shutdown cancels in-flight Android requests", ShutdownCancellation)
};
var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run().WaitAsync(TimeSpan.FromSeconds(10)); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} USB/CTAPHID tests passed.");
return failures == 0 ? 0 : 1;

static Task Descriptors()
{
    Check(UsbDescriptors.Device.Length == 18 && UsbDescriptors.Device[7] == 64);
    Check(UsbDescriptors.Device[4] == 0 && UsbDescriptors.Device[17] == 1);
    var config = UsbDescriptors.Configuration;
    Check(BinaryPrimitives.ReadUInt16LittleEndian(config[2..]) == config.Length && config.Length == 41);
    Check(config[14] == 3 && config[15] == 0 && config[16] == 0);
    Check(config[29] == 0x81 && config[36] == 1 && config[30] == 3 && config[37] == 3);
    Check(BinaryPrimitives.ReadUInt16LittleEndian(config[31..]) == 64 && BinaryPrimitives.ReadUInt16LittleEndian(config[38..]) == 64);
    Check(UsbDescriptors.Report.Length == 34 && UsbDescriptors.Report[..5].SequenceEqual(new byte[] { 6, 0xd0, 0xf1, 9, 1 }));
    Check(BinaryPrimitives.ReadUInt16LittleEndian(UsbDescriptors.Hid[7..]) == UsbDescriptors.Report.Length);
    Check(UsbDescriptors.GetDescriptor(6, 0) is null && UsbDescriptors.StringDescriptor(0xee) is null);
    return Task.CompletedTask;
}

static async Task Init()
{
    await using var device = new CtapHidDevice(new TestTransport());
    var a = await Channel(device);
    var b = await Channel(device);
    Check(a != b && a != 0 && a != uint.MaxValue);
    byte[] nonce = [0, 1, 2, 3, 4, 5, 6, 7];
    await device.SendReportAsync(HidFraming.Fragment(a, CtapHidCommand.Init, nonce)[0]);
    var reply = await Message(device);
    Check(reply.Channel == a && reply.Command == CtapHidCommand.Init && reply.Payload.Length == 17);
    Check(reply.Payload.AsSpan(0, 8).SequenceEqual(nonce));
    Check(BinaryPrimitives.ReadUInt32BigEndian(reply.Payload.AsSpan(8)) == a);
    Check(reply.Payload[12] == 2 && reply.Payload[16] == 0x0c);
}

static async Task FragmentBoundaries()
{
    await using var device = new CtapHidDevice(new TestTransport());
    var channel = await Channel(device);
    foreach (var length in new[] { 0, 1, 56, 57, 58, 115, 116, 117, 7608, 7609 })
    {
        var payload = Enumerable.Range(0, length).Select(i => (byte)(i * 17)).ToArray();
        foreach (var packet in HidFraming.Fragment(channel, CtapHidCommand.Ping, payload)) await device.SendReportAsync(packet);
        var reply = await Message(device);
        Check(reply.Command == CtapHidCommand.Ping && reply.Payload.SequenceEqual(payload));
    }
}

static Task OversizedFragment()
{
    try { HidFraming.Fragment(1, CtapHidCommand.Ping, new byte[7610]); throw new Exception("Oversized message accepted"); }
    catch (ArgumentOutOfRangeException) { }
    return Task.CompletedTask;
}

static async Task InvalidChannels()
{
    await using var device = new CtapHidDevice(new TestTransport());
    foreach (var channel in new uint[] { 0, 17, uint.MaxValue })
    {
        await device.SendReportAsync(HidFraming.Fragment(channel, CtapHidCommand.Ping, [1])[0]);
        await Error(device, CtapHidError.InvalidChannel);
    }
}

static async Task InvalidCommands()
{
    var transport = new TestTransport();
    await using var device = new CtapHidDevice(transport);
    var channel = await Channel(device);
    await device.SendReportAsync(HidFraming.Fragment(channel, CtapHidCommand.Cbor, [])[0]);
    await Error(device, CtapHidError.InvalidLength);
    await device.SendReportAsync(HidFraming.Fragment(channel, CtapHidCommand.Msg, [0, 1])[0]);
    await Error(device, CtapHidError.InvalidCommand);
    var oversized = HidFraming.Fragment(channel, CtapHidCommand.Cbor, [4])[0];
    BinaryPrimitives.WriteUInt16BigEndian(oversized.AsSpan(5), 7610);
    await device.SendReportAsync(oversized);
    await Error(device, CtapHidError.InvalidLength);
    Check(transport.Calls == 0);
}

static async Task InvalidSequence()
{
    await using var device = new CtapHidDevice(new TestTransport());
    var channel = await Channel(device);
    var reports = HidFraming.Fragment(channel, CtapHidCommand.Ping, new byte[100]);
    await device.SendReportAsync(reports[0]);
    reports[1][4] = 1;
    await device.SendReportAsync(reports[1]);
    await Error(device, CtapHidError.InvalidSequence);
    await device.SendReportAsync(HidFraming.Fragment(channel, CtapHidCommand.Ping, [8])[0]);
    Check((await Message(device)).Payload.SequenceEqual(new byte[] { 8 }));
}

static async Task AssemblyTimeout()
{
    await using var device = new CtapHidDevice(new TestTransport(), assemblyTimeout: TimeSpan.FromMilliseconds(100));
    var channel = await Channel(device);
    await device.SendReportAsync(HidFraming.Fragment(channel, CtapHidCommand.Ping, new byte[100])[0]);
    await Error(device, CtapHidError.MessageTimeout);
    await device.SendReportAsync(HidFraming.Fragment(channel, CtapHidCommand.Ping, [9])[0]);
    Check((await Message(device)).Payload[0] == 9);
}

static async Task ConcurrentChannels()
{
    var transport = new TestTransport { Wait = true };
    await using var device = new CtapHidDevice(transport);
    var a = await Channel(device); var b = await Channel(device);
    await device.SendReportAsync(HidFraming.Fragment(a, CtapHidCommand.Cbor, [4])[0]);
    await transport.Started.Task;
    await device.SendReportAsync(HidFraming.Fragment(b, CtapHidCommand.Ping, [1])[0]);
    await Error(device, CtapHidError.ChannelBusy);
    transport.Release.TrySetResult([0, 0xa0]);
    Check((await Cbor(device)).Payload.SequenceEqual(new byte[] { 0, 0xa0 }));
}

static async Task Cancellation()
{
    var transport = new TestTransport { Wait = true };
    await using var device = new CtapHidDevice(transport);
    var channel = await Channel(device);
    await device.SendReportAsync(HidFraming.Fragment(channel, CtapHidCommand.Cbor, [4])[0]);
    await transport.Started.Task;
    await device.SendReportAsync(HidFraming.Fragment(channel, CtapHidCommand.Cancel, [])[0]);
    Check((await Cbor(device)).Payload[0] == 0x2d);
    await transport.Cancelled.Task;
}

static async Task Keepalive()
{
    var transport = new TestTransport { Wait = true };
    await using var device = new CtapHidDevice(transport);
    var channel = await Channel(device);
    await device.SendReportAsync(HidFraming.Fragment(channel, CtapHidCommand.Cbor, [4])[0]);
    var keepalive = await Message(device);
    Check(keepalive.Command == CtapHidCommand.KeepAlive && keepalive.Payload.SequenceEqual(new byte[] { 1 }));
    transport.Release.TrySetResult([0, 0xa0]);
    Check((await Cbor(device)).Payload[0] == 0);
}

static async Task TransactionTimeout()
{
    var transport = new TestTransport { Wait = true };
    await using var device = new CtapHidDevice(transport, transactionTimeout: TimeSpan.FromMilliseconds(100));
    var channel = await Channel(device);
    await device.SendReportAsync(HidFraming.Fragment(channel, CtapHidCommand.Cbor, [4])[0]);
    Check((await Cbor(device)).Payload[0] == 0x3a);
    await transport.Cancelled.Task;
}

static async Task Resynchronization()
{
    var transport = new TestTransport { Wait = true };
    await using var device = new CtapHidDevice(transport);
    var channel = await Channel(device);
    await device.SendReportAsync(HidFraming.Fragment(channel, CtapHidCommand.Cbor, [4])[0]);
    await transport.Started.Task;
    await device.SendReportAsync(HidFraming.Fragment(channel, CtapHidCommand.Init, new byte[8])[0]);
    Check((await Message(device)).Command == CtapHidCommand.Init);
    await transport.Cancelled.Task;
    await device.SendReportAsync(HidFraming.Fragment(channel, CtapHidCommand.Ping, [22])[0]);
    var response = await Message(device);
    Check(response.Command == CtapHidCommand.Ping && response.Payload[0] == 22);
    using var shortWait = new CancellationTokenSource(100);
    try { await device.ReadReportAsync(shortWait.Token); throw new Exception("Stale operation produced output"); }
    catch (OperationCanceledException) { }
}

static async Task TransportFault()
{
    await using var device = new CtapHidDevice(new TestTransport { Throw = true });
    var channel = await Channel(device);
    await device.SendReportAsync(HidFraming.Fragment(channel, CtapHidCommand.Cbor, [4])[0]);
    Check((await Cbor(device)).Payload.SequenceEqual(new byte[] { 0x7f }));
}

static async Task OutputBackpressure()
{
    await using var device = new CtapHidDevice(new TestTransport());
    var channel = await Channel(device);
    var packets = HidFraming.Fragment(channel, CtapHidCommand.Ping, new byte[7609]);
    var failed = false;
    for (var repetition = 0; repetition < 4 && !failed; repetition++)
    {
        try { foreach (var packet in packets) await device.SendReportAsync(packet); }
        catch (IOException) { failed = true; }
    }
    Check(failed);
}

static async Task Devlist()
{
    await using var server = new TestServer();
    using var client = await Wire.Connect(server.Port);
    await client.Send(Wire.Management(0x8005));
    var response = await client.Read(328);
    Check(BinaryPrimitives.ReadUInt16BigEndian(response) == 0x0111 && BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2)) == 5);
    Check(BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(8)) == 1);
    Check(response.AsSpan(12, 312).SequenceEqual(UsbDescriptors.UsbIpDeviceRecord()));
    Check(response.AsSpan(324).SequenceEqual(new byte[] { 3, 0, 0, 0 }));
    Check(!server.Server.IsImported);
}

static async Task ChunkedRequests()
{
    await using var server = new TestServer();
    using var client = await Wire.Connect(server.Port);
    var request = Wire.Management(0x8003).Concat(Wire.Bus("1-1")).ToArray();
    foreach (var b in request) await client.Send([b]);
    Check((await client.Read(320))[7] == 0);
    var urb = Wire.Urb(1, 1, 0, 18, [0x80, 6, 0, 1, 0, 0, 18, 0]);
    foreach (var b in urb) await client.Send([b]);
    var response = await client.Reply();
    Check(response.Status == 0 && response.Data.SequenceEqual(UsbDescriptors.Device.ToArray()));
}

static async Task ExclusiveImport()
{
    await using var server = new TestServer();
    using var first = await Wire.Import(server.Port);
    Check(server.Server.IsImported);
    using var second = await Wire.Connect(server.Port);
    await second.Send(Wire.Management(0x8003).Concat(Wire.Bus("1-1")).ToArray());
    Check(BinaryPrimitives.ReadUInt32BigEndian((await second.Read(8)).AsSpan(4)) == 2);
    using var third = await Wire.Connect(server.Port);
    await third.Send(Wire.Management(0x8003).Concat(Wire.Bus("wrong")).ToArray());
    Check(BinaryPrimitives.ReadUInt32BigEndian((await third.Read(8)).AsSpan(4)) == 1);
}

static async Task Enumeration()
{
    await using var server = new TestServer();
    using var client = await Wire.Import(server.Port);
    await client.Send(Wire.Urb(1, 1, 0, 8, [0x80, 6, 0, 1, 0, 0, 8, 0]));
    Check((await client.Reply()).Data.SequenceEqual(UsbDescriptors.Device[..8].ToArray()));
    await client.Send(Wire.Urb(2, 1, 0, 255, [0x80, 6, 0, 2, 0, 0, 255, 0]));
    Check((await client.Reply()).Data.SequenceEqual(UsbDescriptors.Configuration.ToArray()));
    await client.Send(Wire.Urb(3, 1, 0, 34, [0x81, 6, 0, 0x22, 0, 0, 34, 0]));
    Check((await client.Reply()).Data.SequenceEqual(UsbDescriptors.Report.ToArray()));
    await client.Send(Wire.Urb(4, 1, 0, 10, [0x80, 6, 0, 6, 0, 0, 10, 0]));
    Check((await client.Reply()).Status == -32);
    await client.Configure();
    await client.Send(Wire.Urb(7, 1, 0, 1, [0x80, 8, 0, 0, 0, 0, 1, 0]));
    Check((await client.Reply()).Data.SequenceEqual(new byte[] { 1 }));
}

static async Task InterruptRoundTrip()
{
    await using var server = new TestServer();
    using var client = await Wire.Import(server.Port);
    await client.Configure();
    var channel = await client.Init();
    await client.Send(Wire.Urb(12, 1, 1, 64));
    var ping = HidFraming.Fragment(channel, CtapHidCommand.Ping, [1, 3, 5, 7])[0];
    await client.Send(Wire.Urb(13, 0, 1, 64, payload: ping));
    var a = await client.Reply(); var b = await client.Reply();
    var input = a.Sequence == 12 ? a : b;
    var output = a.Sequence == 13 ? a : b;
    Check(output.Status == 0 && output.ActualLength == 64 && output.Data.Length == 0);
    Check(input.Status == 0 && input.Data.SequenceEqual(ping));
}

static async Task Unlink()
{
    await using var server = new TestServer();
    using var client = await Wire.Import(server.Port);
    await client.Configure();
    var channel = await client.Init();
    await client.Send(Wire.Urb(20, 1, 1, 64));
    var unlink = new byte[48];
    BinaryPrimitives.WriteUInt32BigEndian(unlink, 2);
    BinaryPrimitives.WriteUInt32BigEndian(unlink.AsSpan(4), 21);
    BinaryPrimitives.WriteUInt32BigEndian(unlink.AsSpan(20), 20);
    await client.Send(unlink);
    var removed = await client.Reply();
    Check(removed.Command == 4 && removed.Sequence == 21 && removed.Status == -104);
    await client.Send(Wire.Urb(22, 1, 1, 64));
    var ping = HidFraming.Fragment(channel, CtapHidCommand.Ping, [10])[0];
    await client.Send(Wire.Urb(23, 0, 1, 64, payload: ping));
    var a = await client.Reply(); var b = await client.Reply();
    Check(new[] { a.Sequence, b.Sequence }.Order().SequenceEqual(new uint[] { 22, 23 }));
    Check((a.Sequence == 22 ? a : b).Data.SequenceEqual(ping));
}

static async Task OversizedUrb()
{
    await using var server = new TestServer();
    using var client = await Wire.Import(server.Port);
    await client.Send(Wire.Urb(1, 1, 0, 65537));
    try { await client.Read(1); throw new Exception("Malformed transfer connection remained open"); }
    catch (EndOfStreamException) { }
}

static async Task NetworkBackpressure()
{
    await using var server = new TestServer();
    using var client = await Wire.Import(server.Port);
    await client.Configure();
    var channel = await client.Init();
    var reports = HidFraming.Fragment(channel, CtapHidCommand.Ping, new byte[7609]);
    uint sequence = 100;
    var closed = false;
    for (var message = 0; message < 3 && !closed; message++)
    {
        foreach (var report in reports)
        {
            try
            {
                await client.Send(Wire.Urb(sequence++, 0, 1, 64, payload: report));
                Check((await client.Reply()).Status == 0);
            }
            catch (IOException) { closed = true; break; }
        }
    }
    Check(closed);
    for (var i = 0; i < 20 && server.Server.IsImported; i++) await Task.Delay(10);
    Check(!server.Server.IsImported);
}

static async Task ShutdownCancellation()
{
    var transport = new TestTransport { Wait = true };
    var server = new TestServer(transport);
    using var client = await Wire.Import(server.Port);
    await client.Configure();
    var channel = await client.Init();
    await client.Send(Wire.Urb(40, 0, 1, 64, payload: HidFraming.Fragment(channel, CtapHidCommand.Cbor, [4])[0]));
    Check((await client.Reply()).Status == 0);
    await transport.Started.Task;
    await server.DisposeAsync();
    await transport.Cancelled.Task;
}

static async Task<uint> Channel(CtapHidDevice device)
{
    var nonce = new byte[] { 4, 5, 6, 7, 8, 9, 10, 11 };
    await device.SendReportAsync(HidFraming.Fragment(uint.MaxValue, CtapHidCommand.Init, nonce)[0]);
    var message = await Message(device);
    Check(message.Command == CtapHidCommand.Init && message.Channel == uint.MaxValue);
    return BinaryPrimitives.ReadUInt32BigEndian(message.Payload.AsSpan(8));
}

static async Task<(uint Channel, CtapHidCommand Command, byte[] Payload)> Message(CtapHidDevice device)
{
    using var deadline = new CancellationTokenSource(2000);
    var first = await device.ReadReportAsync(deadline.Token);
    var channel = BinaryPrimitives.ReadUInt32BigEndian(first);
    Check((first[4] & 0x80) != 0);
    var command = (CtapHidCommand)(first[4] & 0x7f);
    var length = BinaryPrimitives.ReadUInt16BigEndian(first.AsSpan(5));
    var payload = new byte[length];
    var offset = Math.Min(57, (int)length);
    first.AsSpan(7, offset).CopyTo(payload);
    byte sequence = 0;
    while (offset < length)
    {
        var continuation = await device.ReadReportAsync(deadline.Token);
        Check(BinaryPrimitives.ReadUInt32BigEndian(continuation) == channel && continuation[4] == sequence++);
        var count = Math.Min(59, length - offset);
        continuation.AsSpan(5, count).CopyTo(payload.AsSpan(offset));
        offset += count;
    }
    return (channel, command, payload);
}

static async Task<(uint Channel, CtapHidCommand Command, byte[] Payload)> Cbor(CtapHidDevice device)
{
    for (var i = 0; i < 10; i++)
    {
        var message = await Message(device);
        if (message.Command == CtapHidCommand.KeepAlive) continue;
        Check(message.Command == CtapHidCommand.Cbor);
        return message;
    }
    throw new Exception("No CTAP response");
}

static async Task Error(CtapHidDevice device, CtapHidError error)
{
    var response = await Message(device);
    Check(response.Command == CtapHidCommand.Error && response.Payload.SequenceEqual(new byte[] { (byte)error }));
}

static void Check(bool condition)
{
    if (!condition) throw new Exception("Assertion failed");
}

sealed class TestTransport : ICtapTransport
{
    public bool Wait { get; init; }
    public bool Throw { get; init; }
    public int Calls;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<byte[]> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        Started.TrySetResult();
        if (Throw) throw new IOException("Test transport failure");
        if (Wait)
        {
            try { return await Release.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
        }
        return [0, .. request];
    }
}

sealed class TestServer : IAsyncDisposable
{
    public UsbIpServer Server { get; }
    public int Port => Server.BoundPort;
    private readonly Task _running;
    public TestServer(ICtapTransport? transport = null)
    {
        Server = new UsbIpServer(transport ?? new TestTransport(), 0);
        _running = Server.StartAsync();
    }
    public async ValueTask DisposeAsync() { await Server.DisposeAsync(); await _running; }
}

sealed class Wire : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private Wire(TcpClient client) { _client = client; _stream = client.GetStream(); }
    public static async Task<Wire> Connect(int port)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port);
        return new Wire(client);
    }
    public static async Task<Wire> Import(int port)
    {
        var result = await Connect(port);
        await result.Send(Management(0x8003).Concat(Bus("1-1")).ToArray());
        var reply = await result.Read(320);
        if (BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(4)) != 0) throw new Exception("Import failed");
        return result;
    }
    public async Task Configure()
    {
        await Send(Urb(5, 0, 0, 0, [0, 9, 1, 0, 0, 0, 0, 0]));
        if ((await Reply()).Status != 0) throw new Exception("Configuration failed");
    }
    public async Task<uint> Init()
    {
        await Send(Urb(10, 1, 1, 64));
        await Send(Urb(11, 0, 1, 64, payload: HidFraming.Fragment(uint.MaxValue, CtapHidCommand.Init, new byte[8])[0]));
        var a = await Reply(); var b = await Reply();
        var input = a.Sequence == 10 ? a : b;
        return BinaryPrimitives.ReadUInt32BigEndian(input.Data.AsSpan(15));
    }
    public ValueTask Send(byte[] data) => _stream.WriteAsync(data);
    public async Task<byte[]> Read(int length)
    {
        using var deadline = new CancellationTokenSource(2000);
        var result = new byte[length];
        await _stream.ReadExactlyAsync(result, deadline.Token);
        return result;
    }
    public async Task<ReplyData> Reply()
    {
        var header = await Read(48);
        var command = BinaryPrimitives.ReadUInt32BigEndian(header);
        var sequence = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        var status = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20));
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(24));
        // Tests know which requests were IN; only IN data is present on the wire.
        var input = sequence is 1 or 2 or 3 or 4 or 7 or 10 or 12 or 20 or 22;
        var data = command == 3 && input && status == 0 && length > 0 ? await Read(length) : [];
        return new ReplyData(command, sequence, status, length, data);
    }
    public static byte[] Management(ushort command)
    {
        var result = new byte[8];
        BinaryPrimitives.WriteUInt16BigEndian(result, 0x0111);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(2), command);
        return result;
    }
    public static byte[] Bus(string bus)
    {
        var result = new byte[32];
        Encoding.ASCII.GetBytes(bus).CopyTo(result, 0);
        return result;
    }
    public static byte[] Urb(uint sequence, uint direction, uint endpoint, int length, byte[]? setup = null, byte[]? payload = null)
    {
        var result = new byte[48 + (payload?.Length ?? 0)];
        BinaryPrimitives.WriteUInt32BigEndian(result, 1);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8), UsbDescriptors.DeviceId);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12), direction);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16), endpoint);
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(24), length);
        if (setup is not null) setup.CopyTo(result, 40);
        payload?.CopyTo(result, 48);
        return result;
    }
    public void Dispose() => _client.Dispose();
    public sealed record ReplyData(uint Command, uint Sequence, int Status, int ActualLength, byte[] Data);
}
