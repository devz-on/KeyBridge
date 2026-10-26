using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using KeyBridge.Core;

// Synthetic peers exist ONLY in this test assembly. Production has no Windows credential-signing backend.
var tests = new (string Name, Func<Task> Run)[] {
    ("binary framing survives arbitrary stream fragmentation", TestFrameRoundTrip),
    ("framing rejects unknown version, reserved bits, zero ID, oversize and truncation", TestInvalidFrames),
    ("pairing SAS binds ordered DER identities", TestPairingCode),
    ("ADB discovers authorized, unauthorized and offline devices", TestAdbParsing),
    ("USB forwarding refuses wireless device paths", TestUsbOnly),
    ("mutual TLS pairing gates CTAP and preserves request correlation", TestTlsPairing),
    ("changed phone certificate is rejected before application requests", TestPinMismatch),
    ("mismatched response kinds fail closed", TestWrongKind),
    ("cancellation sends matching request ID and terminates approval session", TestCancellation),
    ("phone disconnect fails pending CTAP promptly", TestDisconnect)
    ,("malformed phone frames terminate pending requests and paired session", TestMalformedResponse)
    ,("concurrent requests use increasing wire IDs", TestConcurrentRequests)
    ,("a mismatched service preface cannot downgrade to plaintext", TestUpgradeMismatch)
    ,("wireless mDNS and IP transports require explicit opt-in", TestWirelessDiscovery)
    ,("ADB alias recognition requires one unambiguous existing certificate pin", TestPinAliases)
    ,("phone readiness distinguishes unknown, unavailable and available biometrics without signing", TestPhoneReadiness)
};
int failures = 0;
foreach (var test in tests)
{
    try { await test.Run().WaitAsync(TimeSpan.FromSeconds(20)); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + test.Name + "\n" + ex); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} transport tests passed.");
return failures == 0 ? 0 : 1;

static void Assert(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
static async Task TestPhoneReadiness()
{
    foreach (bool? available in new bool?[] { null, false, true })
    {
        await using var fixture = new TestPhone { BiometricAvailable = available };
        await using var client = new PhoneClient(fixture.HostCertificate);
        var hello = await client.ConnectAsync(fixture.Port, HostIdentity.Fingerprint(fixture.PhoneCertificate), "Readiness test");
        Assert(hello.Paired && hello.BiometricAvailable == available);
        Assert(fixture.CtapRequests == 0, "Reading readiness must not use a credential key.");
    }
}
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
static async Task TestFrameRoundTrip()
{
    byte[] data = RandomNumberGenerator.GetBytes(65536);
    using var stream = new MemoryStream();
    await BridgeProtocol.WriteAsync(stream, new(0x83, uint.MaxValue, data));
    using var fragmented = new FragmentedStream(stream.ToArray());
    var actual = await BridgeProtocol.ReadAsync(fragmented);
    Assert(actual.Kind == 0x83 && actual.RequestId == uint.MaxValue && actual.Payload.SequenceEqual(data));
}
static async Task TestInvalidFrames()
{
    foreach (int field in new[] { 0, 4, 6, 7, 8, 12 })
    {
        using var stream = new MemoryStream();
        await BridgeProtocol.WriteAsync(stream, new(1, 1, []));
        var bytes = stream.ToArray();
        if (field == 8) Array.Clear(bytes, 8, 4);
        else if (field == 12) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), 65537);
        else bytes[field] ^= 0xff;
        await Throws<InvalidDataException>(() => BridgeProtocol.ReadAsync(new MemoryStream(bytes)));
    }
    await Throws<EndOfStreamException>(() => BridgeProtocol.ReadAsync(new MemoryStream(new byte[3])));
    await Throws<InvalidDataException>(() => BridgeProtocol.WriteAsync(new MemoryStream(), new(1, 0, [])));
    await Throws<InvalidDataException>(() => BridgeProtocol.WriteAsync(new MemoryStream(), new(1, 1, new byte[65537])));
}
static Task TestPairingCode()
{
    byte[] phone = [1, 2, 3], desktop = [4, 5];
    string code = BridgeProtocol.PairingCode(phone, desktop);
    byte[] bytes = Encoding.UTF8.GetBytes("KeyBridge pairing v1").Concat(phone).Concat(desktop).ToArray();
    Assert(code == Convert.ToHexString(SHA256.HashData(bytes)));
    Assert(code != BridgeProtocol.PairingCode(desktop, phone));
    return Task.CompletedTask;
}
static Task TestAdbParsing()
{
    var devices = AdbManager.ParseDevices("List of devices attached\r\nPHONE1 device usb:1-3 product:x model:iQOO_Neo_10R transport_id:1\r\nPHONE2 unauthorized usb:1-2\r\nPHONE3 offline\r\n* daemon started successfully\r\n");
    Assert(devices.Count == 3 && devices[0].Model == "iQOO Neo 10R" && devices[1].State == "unauthorized" && devices[2].State == "offline");
    return Task.CompletedTask;
}
static async Task TestUsbOnly()
{
    var adb = new TestAdb("local:emulator-5554");
    await Throws<IOException>(() => adb.ForwardAsync("phone"));
    Assert(adb.Invocations == 1);
    adb = new TestAdb("usb:1-2");
    Assert(await adb.ForwardAsync("phone") == 43210 && adb.Invocations == 2);
}
static async Task TestTlsPairing()
{
    await using var fixture = new TestPhone();
    await using var client = new PhoneClient(fixture.HostCertificate);
    var hello = await client.ConnectAsync(fixture.Port, null, "Synthetic test host");
    Assert(!hello.Paired && !client.IsPaired && hello.PairingCode.Length == 64);
    await Throws<AuthenticationException>(() => client.ExchangeAsync([4], CancellationToken.None));
    await client.PairAsync("Synthetic test host");
    Assert(client.IsPaired);
    var info = await client.ExchangeAsync([4], CancellationToken.None);
    Assert(info.SequenceEqual(new byte[] { 0, 0xa0 }));
    var status = await client.StatusAsync();
    Assert(status.GetProperty("credentialCount").GetInt32() == 0);
    Assert(fixture.CtapRequests == 1);
}
static async Task TestPinMismatch()
{
    await using var fixture = new TestPhone();
    await using var client = new PhoneClient(fixture.HostCertificate);
    await Throws<AuthenticationException>(() => client.ConnectAsync(fixture.Port, new string('0', 64), "test"));
    Assert(fixture.CtapRequests == 0);
}
static async Task TestWrongKind()
{
    await using var fixture = new TestPhone { WrongKind = true };
    await using var client = new PhoneClient(fixture.HostCertificate);
    await client.ConnectAsync(fixture.Port, HostIdentity.Fingerprint(fixture.PhoneCertificate), "test");
    await Throws<InvalidDataException>(() => client.ExchangeAsync([4], CancellationToken.None));
    Assert(!client.IsPaired && !client.IsConnected);
}
static async Task TestCancellation()
{
    await using var fixture = new TestPhone { HoldCtap = true };
    await using var client = new PhoneClient(fixture.HostCertificate);
    await client.ConnectAsync(fixture.Port, HostIdentity.Fingerprint(fixture.PhoneCertificate), "test");
    using var cancellation = new CancellationTokenSource();
    var request = client.ExchangeAsync([2, 0xa0], cancellation.Token);
    await fixture.CtapStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancellation.Cancel();
    await Throws<OperationCanceledException>(() => request);
    await fixture.CancelObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert(fixture.LastCtapId == fixture.LastCancelId && !client.IsConnected && !client.IsPaired);
}
static async Task TestDisconnect()
{
    await using var fixture = new TestPhone { DropCtap = true };
    await using var client = new PhoneClient(fixture.HostCertificate);
    await client.ConnectAsync(fixture.Port, HostIdentity.Fingerprint(fixture.PhoneCertificate), "test");
    await Throws<IOException>(() => client.ExchangeAsync([4], CancellationToken.None));
    Assert(!client.IsPaired);
}
static async Task TestMalformedResponse()
{
    await using var fixture = new TestPhone { MalformedResponse = true };
    await using var client = new PhoneClient(fixture.HostCertificate);
    await client.ConnectAsync(fixture.Port, HostIdentity.Fingerprint(fixture.PhoneCertificate), "test");
    await Throws<IOException>(() => client.ExchangeAsync([4], CancellationToken.None));
    Assert(!client.IsPaired && !client.IsConnected);
}
static async Task TestConcurrentRequests()
{
    await using var fixture = new TestPhone();
    await using var client = new PhoneClient(fixture.HostCertificate);
    await client.ConnectAsync(fixture.Port, HostIdentity.Fingerprint(fixture.PhoneCertificate), "test");
    await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => client.StatusAsync()));
    Assert(!fixture.ReorderedRequest && fixture.RequestCount == 101 && client.IsPaired);
}
static async Task TestUpgradeMismatch()
{
    await using var fixture = new TestPhone { BadUpgrade = true };
    await using var client = new PhoneClient(fixture.HostCertificate);
    await Throws<InvalidDataException>(() => client.ConnectAsync(fixture.Port, null, "test"));
    Assert(!client.IsConnected && !client.IsPaired && fixture.RequestCount == 0);
}
static async Task TestWirelessDiscovery()
{
    var adb = new DiscoveryAdb();
    var usbOnly = await adb.DiscoverAsync();
    Assert(usbOnly.Count == 1 && !usbOnly[0].IsWireless);
    Assert(!await adb.VerifyTransportAsync("adb-test._adb-tls-connect._tcp"));
    adb.AllowWirelessDebugging = true;
    var enabled = await adb.DiscoverAsync();
    Assert(enabled.Count == 3 && enabled.Count(d => d.IsWireless) == 2);
    Assert(await adb.ForwardAsync("adb-test._adb-tls-connect._tcp") == 43210);
    Assert(!enabled.Any(d => d.Serial.StartsWith("emulator-")));
}
static Task TestPinAliases()
{
    string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KeyBridge.Core.Tests." + Guid.NewGuid().ToString("N")));
    try
    {
        var store = new SettingsStore(root);
        Assert(store.ExpectedPhonePin("new-route") is null);
        store.Settings.PhonePins["usb-route"] = new string('A', 64);
        Assert(store.ExpectedPhonePin("new-route") == new string('A', 64));
        store.Settings.PhonePins["other-route-for-same-phone"] = new string('A', 64);
        Assert(store.ExpectedPhonePin("new-route") == new string('A', 64));
        store.Settings.PhonePins["second-phone"] = new string('B', 64);
        Assert(store.ExpectedPhonePin("new-route") is null);
        Assert(store.ExpectedPhonePin("usb-route") == new string('A', 64));
        return Task.CompletedTask;
    }
    finally
    {
        string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("KeyBridge.Core.Tests.", StringComparison.Ordinal)) throw new IOException("Test cleanup path check failed.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], ct);
}
sealed class TestAdb(string devicePath) : AdbManager
{
    public int Invocations;
    public override Task<CommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken ct = default, int timeoutSeconds = 15)
    {
        Invocations++;
        return Task.FromResult(new CommandResult(0, arguments.Contains("get-devpath") ? devicePath : "43210", ""));
    }
}
sealed class DiscoveryAdb : AdbManager
{
    public override Task<CommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken ct = default, int timeoutSeconds = 15) =>
        Task.FromResult(new CommandResult(0, arguments.Contains("devices")
            ? "List of devices attached\nusb-phone device model:Test_Phone\nadb-test._adb-tls-connect._tcp device model:Test_Phone\n192.0.2.1:12345 device model:Test_Phone\nemulator-5554 device model:Emulator\n"
            : "43210", ""));
}
sealed class TestPhone : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task server;
    public X509Certificate2 PhoneCertificate { get; }
    public X509Certificate2 HostCertificate { get; }
    private readonly string phoneKeyName = "KeyBridge.Test." + Guid.NewGuid().ToString("N");
    private readonly string hostKeyName = "KeyBridge.Test." + Guid.NewGuid().ToString("N");
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public bool WrongKind { get; init; }
    public bool HoldCtap { get; init; }
    public bool DropCtap { get; init; }
    public bool MalformedResponse { get; init; }
    public bool BadUpgrade { get; init; }
    public bool? BiometricAvailable { get; init; }
    public int CtapRequests;
    public int RequestCount;
    public bool ReorderedRequest;
    public uint LastCtapId, LastCancelId;
    public TaskCompletionSource CtapStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TestPhone()
    {
        using var createdPhone = CreateCertificate("Test phone", phoneKeyName);
        using var createdHost = CreateCertificate("Test desktop", hostKeyName);
        // Schannel client authentication uses CurrentUser/My, exactly as the production identity provider does.
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite); store.Add(createdHost); store.Add(createdPhone);
        HostCertificate = store.Certificates.Find(X509FindType.FindByThumbprint, createdHost.Thumbprint, false)[0];
        PhoneCertificate = store.Certificates.Find(X509FindType.FindByThumbprint, createdPhone.Thumbprint, false)[0];
        listener.Start(); server = RunAsync();
    }
    private static X509Certificate2 CreateCertificate(string name, string keyName)
    {
        // Exercise the same persisted non-exportable CNG identity mechanism as production.
        using var cng = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, new CngKeyCreationParameters {
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider, KeyUsage = CngKeyUsages.Signing, ExportPolicy = CngExportPolicies.None });
        using var key = new ECDsaCng(cng);
        var request = new CertificateRequest("CN=" + name + " " + Guid.NewGuid().ToString("N"), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1"), new Oid("1.3.6.1.5.5.7.3.2") }, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
    private async Task RunAsync()
    {
        try
        {
            using var tcp = await listener.AcceptTcpClientAsync(lifetime.Token);
            byte[] upgrade = new byte[8];
            await tcp.GetStream().ReadExactlyAsync(upgrade, lifetime.Token);
            if (!upgrade.AsSpan().SequenceEqual("KBTLS1\r\n"u8)) throw new InvalidDataException("Missing mandatory TLS upgrade.");
            if (BadUpgrade) { await tcp.GetStream().WriteAsync("NO-TLS\r\n"u8.ToArray(), lifetime.Token); return; }
            await tcp.GetStream().WriteAsync(upgrade, lifetime.Token);
            await tcp.GetStream().FlushAsync(lifetime.Token);
            using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => {
                bool matches = certificate is not null && certificate.GetRawCertData().SequenceEqual(HostCertificate.RawData);
                if (!matches) Console.WriteLine("Test client cert matches=" + matches + "; actual=" + certificate?.Subject + "; expected=" + HostCertificate.Subject + "; HasPrivate=" + HostCertificate.HasPrivateKey);
                return matches;
            });
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                ServerCertificateContext = SslStreamCertificateContext.Create(PhoneCertificate, null, offline: true,
                    trust: SslCertificateTrust.CreateForX509Collection(new X509Certificate2Collection(HostCertificate), sendTrustInHandshake: false)),
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = [new SslApplicationProtocol("keybridge/1")],
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck, AllowTlsResume = false
            }, lifetime.Token);
            AssertProtocol(tls.NegotiatedApplicationProtocol == new SslApplicationProtocol("keybridge/1"));
            uint lastRequestId = 0;
            while (!lifetime.IsCancellationRequested)
            {
                var frame = await BridgeProtocol.ReadAsync(tls, lifetime.Token);
                byte[] response;
                if (frame.Kind == (byte)MessageKind.Cancel)
                { LastCancelId = frame.RequestId; CancelObserved.TrySetResult(); continue; }
                RequestCount++;
                if (frame.RequestId <= lastRequestId) ReorderedRequest = true;
                lastRequestId = frame.RequestId;
                if (frame.Kind == (byte)MessageKind.Hello)
                    response = JsonSerializer.SerializeToUtf8Bytes(new { deviceName = "Synthetic test phone", paired = true,
                        pairingCode = BridgeProtocol.PairingCode(PhoneCertificate.RawData, HostCertificate.RawData), credentialCount = 0, hardwareSecurity = "Test fixture", biometricAvailable = BiometricAvailable });
                else if (frame.Kind == (byte)MessageKind.Pair) response = "{\"paired\":true}"u8.ToArray();
                else if (frame.Kind == (byte)MessageKind.Status) response = "{\"credentialCount\":0}"u8.ToArray();
                else
                {
                    CtapRequests++; LastCtapId = frame.RequestId; CtapStarted.TrySetResult();
                    if (DropCtap) return;
                    if (MalformedResponse)
                    {
                        byte[] badHeader = "KBRG\u0002\u0083\0\0\0\0\0\u0001\0\0\0\0".Select(c => (byte)c).ToArray();
                        await tls.WriteAsync(badHeader, lifetime.Token);
                        continue;
                    }
                    if (HoldCtap) continue;
                    response = [0, 0xa0];
                }
                byte kind = (byte)(frame.Kind | 0x80);
                if (WrongKind && frame.Kind == (byte)MessageKind.Ctap) kind = 0x85;
                await BridgeProtocol.WriteAsync(tls, new(kind, frame.RequestId, response), lifetime.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidDataException or AuthenticationException or ObjectDisposedException)
        { }
    }
    private static void AssertProtocol(bool condition) { if (!condition) throw new AuthenticationException("ALPN mismatch."); }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); listener.Stop();
        try { await server; } catch (SocketException) { }
        finally
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite); store.Remove(HostCertificate); store.Remove(PhoneCertificate);
            PhoneCertificate.Dispose(); HostCertificate.Dispose(); lifetime.Dispose();
            using var phoneKey = CngKey.Open(phoneKeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider); phoneKey.Delete();
            using var hostKey = CngKey.Open(hostKeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider); hostKey.Delete();
        }
    }
}
