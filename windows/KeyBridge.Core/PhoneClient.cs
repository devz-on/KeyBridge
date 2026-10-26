using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using KeyBridge.Usb;

namespace KeyBridge.Core;

public sealed record PhoneHello(string DeviceName, bool Paired, string PairingCode, int CredentialCount, string HardwareSecurity, bool? BiometricAvailable = null);

/// <summary>All CTAP payloads go to the phone. This class has no signing implementation.</summary>
public sealed class PhoneClient : ICtapTransport, IAsyncDisposable
{
    private readonly TcpClient tcp = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<BridgeFrame>> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly X509Certificate2 identity;
    private readonly SslProtocols protocols;
    private SslStream? tls;
    private Task? readTask;
    private long nextId;
    private bool paired;
    private int disposed;
    public string PeerFingerprint { get; private set; } = "";
    public string PresentedFingerprint { get; private set; } = "";
    public string PresentedIssuer { get; private set; } = "";
    public string PresentedSubject { get; private set; } = "";
    public string PairingCode { get; private set; } = "";
    public bool IsPaired => paired && !lifetime.IsCancellationRequested;
    public bool IsConnected => tls is not null && !lifetime.IsCancellationRequested;
    public string HandshakeStage { get; private set; } = "Not started";
    public event Action? Disconnected;

    public PhoneClient(X509Certificate2 identity, SslProtocols protocols = SslProtocols.Tls12)
    {
        if (protocols == SslProtocols.None || (protocols & ~(SslProtocols.Tls12 | SslProtocols.Tls13)) != 0)
            throw new ArgumentException("Only explicit TLS 1.2 and/or TLS 1.3 are supported.", nameof(protocols));
        this.identity = identity; this.protocols = protocols;
    }
    public async Task<PhoneHello> ConnectAsync(int port, string? expectedPin, string hostName, CancellationToken ct = default)
    {
        if (tls is not null) throw new InvalidOperationException("Already connected.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        HandshakeStage = "TCP loopback connected; negotiating KeyBridge protocol";
        NetworkStream network = tcp.GetStream();
        byte[] preface = "KBTLS1\r\n"u8.ToArray();
        byte[] replyPreface = new byte[preface.Length];
        try
        {
            await network.WriteAsync(preface, timeout.Token);
            await network.FlushAsync(timeout.Token);
            await network.ReadExactlyAsync(replyPreface, timeout.Token);
            if (!replyPreface.AsSpan().SequenceEqual(preface)) throw new InvalidDataException("The peer did not accept the mandatory KeyBridge TLS upgrade.");
        }
        catch { await DisposeAsync(); throw; }
        HandshakeStage = "KeyBridge protocol accepted; waiting for TLS certificate";
        X509Certificate2? peer = null;
        tls = new SslStream(network, false, (_, certificate, _, _) => {
            if (certificate is null) { HandshakeStage = "TLS peer did not present a certificate"; return false; }
            using var candidate = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
            if (candidate.NotBefore.ToUniversalTime() > DateTime.UtcNow || candidate.NotAfter.ToUniversalTime() <= DateTime.UtcNow) { HandshakeStage = "TLS peer certificate date invalid"; return false; }
            using var publicKey = candidate.GetECDsaPublicKey();
            if (publicKey is null || publicKey.KeySize != 256) { HandshakeStage = "TLS peer certificate is not EC256"; return false; }
            if (publicKey.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7" ||
                !candidate.SubjectName.RawData.AsSpan().SequenceEqual(candidate.IssuerName.RawData))
            { HandshakeStage = "TLS peer is not a self-issued P-256 KeyBridge identity"; return false; }
            string fingerprint = HostIdentity.Fingerprint(candidate);
            PresentedFingerprint = fingerprint;
            PresentedIssuer = candidate.Issuer;
            PresentedSubject = candidate.Subject;
            if (expectedPin is not null && !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expectedPin), Encoding.ASCII.GetBytes(fingerprint))) { HandshakeStage = "TLS peer pin mismatch"; return false; }
            peer = X509CertificateLoader.LoadCertificate(candidate.RawData);
            HandshakeStage = "TLS peer certificate validated; finishing client authentication";
            return true; // First-use trust is limited to Hello/Pair until explicit SAS confirmation on both devices.
        });
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                // Scope Schannel's native session/credential caches to this identity pair, including first use.
                // TCP still connects only to loopback; this SNI label is not resolved through DNS.
                TargetHost = "keybridge-" + HostIdentity.Fingerprint(identity)[..24].ToLowerInvariant() + "." +
                    (expectedPin is null ? "unpaired" : expectedPin[..Math.Min(24, expectedPin.Length)].ToLowerInvariant()) + ".local",
                EnabledSslProtocols = protocols,
                ApplicationProtocols = [new SslApplicationProtocol("keybridge/1")],
                ClientCertificateContext = SslStreamCertificateContext.Create(identity, null, offline: true),
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EncryptionPolicy = EncryptionPolicy.RequireEncryption,
                AllowTlsResume = false
            }, timeout.Token);
            if (tls.NegotiatedApplicationProtocol != new SslApplicationProtocol("keybridge/1"))
                throw new AuthenticationException("The peer did not negotiate the KeyBridge application protocol.");
            if (tls.SslProtocol == SslProtocols.Tls12 && tls.NegotiatedCipherSuite is not
                (TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256 or TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384))
                throw new AuthenticationException("The bridge requires ECDHE-ECDSA with AES-GCM for TLS 1.2.");
            // Schannel can report LocalCertificate/IsMutuallyAuthenticated incorrectly for an explicit context.
            // Android requires a TLS client certificate. Hello binds its actual TLS peer cert into the full hash,
            // which is checked below before any pairing or CTAP operation is allowed.
            if (peer is null) throw new AuthenticationException("The phone did not establish TLS identity.");
            HandshakeStage = "TLS established";
            using (peer)
            {
                PeerFingerprint = HostIdentity.Fingerprint(peer);
                PairingCode = BridgeProtocol.PairingCode(peer.RawData, identity.RawData);
            }
            readTask = ReadLoopAsync();
            var hello = await RequestJsonAsync(MessageKind.Hello, new { hostName }, timeout.Token);
            string remoteCode = GetString(hello, "pairingCode", "");
            if (!string.Equals(remoteCode, PairingCode, StringComparison.OrdinalIgnoreCase)) throw new AuthenticationException("Pairing code does not match the TLS identities.");
            paired = expectedPin is not null && GetBool(hello, "paired");
            return new(GetString(hello, "deviceName", "Android phone"), paired, PairingCode,
                GetInt(hello, "credentialCount"), GetString(hello, "hardwareSecurity", "Unknown"), GetOptionalBool(hello, "biometricAvailable"));
        }
        catch { await DisposeAsync(); throw; }
    }
    public async Task PairAsync(string hostName, CancellationToken ct = default)
    {
        var result = await RequestJsonAsync(MessageKind.Pair, new { hostName }, ct, 120);
        if (!GetBool(result, "paired")) throw new AuthenticationException("Pairing was rejected on the phone.");
        paired = true;
    }
    public async Task<JsonElement> StatusAsync(CancellationToken ct = default)
    {
        EnsurePaired();
        return await RequestJsonAsync(MessageKind.Status, null, ct);
    }
    public async Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken)
    {
        EnsurePaired();
        if (request.Length is < 1 or > BridgeProtocol.MaximumCtap) throw new InvalidDataException("Invalid CTAP request size.");
        return (await RequestAsync(MessageKind.Ctap, request, cancellationToken, 135)).Payload;
    }
    private void EnsurePaired()
    {
        if (!IsPaired) throw new AuthenticationException("Pair and confirm the phone before using the authenticator.");
    }
    private async Task<JsonElement> RequestJsonAsync(MessageKind kind, object? value, CancellationToken ct, int timeoutSeconds = 15)
    {
        byte[] payload = value is null ? [] : JsonSerializer.SerializeToUtf8Bytes(value);
        var frame = await RequestAsync(kind, payload, ct, timeoutSeconds);
        try
        {
            using var document = JsonDocument.Parse(frame.Payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a protocol JSON object.");
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        { await DisposeAsync(); throw; }
    }
    private async Task<BridgeFrame> RequestAsync(MessageKind kind, byte[] payload, CancellationToken ct, int timeoutSeconds)
    {
        if (tls is null || lifetime.IsCancellationRequested) throw new IOException("Phone connection closed.");
        uint id = 0;
        var completion = new TaskCompletionSource<BridgeFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Disconnect faults the completion; linking the stream-lifetime token here would race that IOException.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await writes.WaitAsync(timeout.Token);
            try
            {
                // Allocate in wire order, even when Status and CTAP are sent concurrently.
                long number = Interlocked.Increment(ref nextId);
                if (number > uint.MaxValue) throw new IOException("Session request limit reached. Reconnect the phone.");
                id = (uint)number;
                if (!pending.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate request ID.");
                await BridgeProtocol.WriteAsync(tls, new((byte)kind, id, payload), timeout.Token);
            }
            finally { writes.Release(); }
            var response = await completion.Task.WaitAsync(timeout.Token);
            if (response.Kind == (byte)MessageKind.Error)
                throw new IOException("The phone rejected the request. Check the approval screen and phone diagnostics.");
            if (response.Kind != ((byte)kind | 0x80)) throw new InvalidDataException("Mismatched response kind.");
            if (kind == MessageKind.Ctap && response.Payload.Length is < 1 or > BridgeProtocol.MaximumCtap)
                throw new InvalidDataException("Invalid CTAP response size.");
            return response;
        }
        catch (OperationCanceledException)
        {
            if (kind == MessageKind.Ctap && !lifetime.IsCancellationRequested)
            {
                try
                {
                    using var cancelTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await writes.WaitAsync(cancelTimeout.Token);
                    try { await BridgeProtocol.WriteAsync(tls, new((byte)MessageKind.Cancel, id, []), cancelTimeout.Token); }
                    finally { writes.Release(); }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            // A cancelled transaction cannot leave approval state reusable by a later request.
            await DisposeAsync();
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or AuthenticationException or ObjectDisposedException)
        {
            await DisposeAsync();
            throw;
        }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task ReadLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var frame = await BridgeProtocol.ReadAsync(tls!, lifetime.Token);
                if ((frame.Kind & 0x80) == 0) throw new InvalidDataException("Unexpected phone request.");
                if (pending.TryGetValue(frame.RequestId, out var completion))
                {
                    if (!completion.TrySetResult(frame)) throw new InvalidDataException("Duplicate response ID.");
                }
                else throw new InvalidDataException("Unexpected or replayed response ID.");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException or AuthenticationException)
        {
            System.Diagnostics.Debug.WriteLine("KeyBridge transport closed: " + ex.GetType().Name);
            paired = false;
            foreach (var item in pending.Values) item.TrySetException(new IOException("Phone connection closed.", ex));
            lifetime.Cancel();
            Disconnected?.Invoke();
        }
    }
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return ValueTask.CompletedTask;
        paired = false;
        lifetime.Cancel();
        tcp.Dispose();
        tls?.Dispose();
        foreach (var item in pending.Values) item.TrySetException(new IOException("Phone connection closed."));
        Disconnected?.Invoke();
        return ValueTask.CompletedTask;
    }
    internal static string GetString(JsonElement element, string property, string fallback) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    internal static int GetInt(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.TryGetInt32(out int number) ? number : 0;
    internal static bool GetBool(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;
    internal static bool? GetOptionalBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}
