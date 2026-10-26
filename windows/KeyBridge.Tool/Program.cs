using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Security.Authentication;
using System.Text;
using KeyBridge.Core;

// An explicit developer diagnostic. Never stores a FIDO private key or substitutes a phone.
if (args.Length == 0) { Help(); return 0; }
string command = args[0];
var settings = new SettingsStore();
if (command is "enable-wireless" or "disable-wireless")
{
    settings.Settings.AllowWirelessDebugging = command == "enable-wireless";
    settings.Save();
    Console.WriteLine("Wireless ADB discovery " + (settings.Settings.AllowWirelessDebugging ? "enabled" : "disabled") + ". Reopen the companion to load this preference. Android wireless-debugging authorization is managed on the phone.");
    return 0;
}
if (command == "bridge")
{
    await using var controller = new BridgeController();
    string? lastEvent = null;
    controller.Changed += snapshot => {
        string? nextEvent = snapshot.Activity.FirstOrDefault();
        if (nextEvent is not null && nextEvent != lastEvent) { lastEvent = nextEvent; Console.WriteLine(nextEvent); }
    };
    if (args.Contains("--wireless")) controller.AllowWirelessDebugging = true;
    string? usbipPath = Option("--usbip");
    if (usbipPath is not null) controller.UsbIpPath = usbipPath;
    await controller.RefreshAsync();
    string? requestedSerial = Option("--serial") ?? (controller.Snapshot.Devices.Count(d => d.State == "device") == 1 ? controller.Snapshot.Devices.First(d => d.State == "device").Serial : null);
    if (requestedSerial is null) { Console.Error.WriteLine("Select one authorized phone with --serial."); return 1; }
    try
    {
        await controller.ConnectAsync(requestedSerial);
        if (!controller.Snapshot.Paired) throw new IOException("Pair the phone explicitly before starting the virtual device.");
        await controller.StartVirtualAsync();
        Console.WriteLine(controller.Snapshot.State + ": " + controller.Snapshot.Detail);
        if (args.Contains("--attach"))
        {
            if (!args.Contains("--driver-consent")) throw new IOException("Explicit --driver-consent is required for driver attachment.");
            controller.DriverConsent = true;
            await controller.AttachAsync();
            Console.WriteLine(controller.Snapshot.State + ": " + controller.Snapshot.Detail);
        }
        Console.WriteLine("Bridge running. Press Ctrl+C to stop and detach its USB/IP session.");
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
        try { await Task.Delay(Timeout.Infinite, shutdown.Token); } catch (OperationCanceledException) { }
        await controller.StopAsync();
        return 0;
    }
    catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
}
var adb = new AdbManager { Executable = settings.Settings.AdbPath, AllowWirelessDebugging = args.Contains("--wireless") || settings.Settings.AllowWirelessDebugging };
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; timeout.Cancel(); };
int? port = null;
string? serial = null;
try
{
    var devices = await adb.DiscoverAsync(timeout.Token);
    if (command == "devices")
    { foreach (var device in devices) Console.WriteLine($"{device.Serial}\t{device.State}\t{device.Model}"); return 0; }
    serial = Option("--serial") ?? (devices.Count(d => d.State == "device") == 1 ? devices.First(d => d.State == "device").Serial : null);
    if (serial is null) throw new IOException("Select one authorized phone with --serial SERIAL; wireless debugging additionally requires --wireless.");
    port = await adb.ForwardAsync(serial, timeout.Token);
    using var identity = HostIdentity.LoadOrCreate();
    await using var phone = new PhoneClient(identity);
    string? pin = settings.ExpectedPhonePin(serial);
    PhoneHello hello;
    try { hello = await phone.ConnectAsync(port.Value, pin, Environment.MachineName, timeout.Token); }
    catch
    {
        Console.Error.WriteLine("Transport stage: " + phone.HandshakeStage);
        if (phone.HandshakeStage == "TLS peer pin mismatch")
            Console.Error.WriteLine("The presented phone certificate has SHA256 fingerprint " + phone.PresentedFingerprint + ". Subject: " + phone.PresentedSubject + "; issuer: " + phone.PresentedIssuer + ". No trust pin was changed.");
        throw;
    }
    Console.WriteLine($"Phone: {hello.DeviceName}; ADB transport: {(AdbManager.IsWirelessSerial(serial) ? "wireless" : "USB")}; paired: {phone.IsPaired}; pairing fingerprint: {phone.PairingCode}");
    if (phone.IsPaired && !settings.Settings.PhonePins.ContainsKey(serial))
    { settings.Settings.PhonePins[serial] = phone.PeerFingerprint; settings.Save(); }
    if (command == "hello") return 0;
    if (command == "pair")
    {
        string? confirmedCode = Option("--code");
        if (!string.Equals(confirmedCode, phone.PairingCode, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Compare this code on the phone, then repeat pair with --code MATCHING_CODE. Never approve a different code.");
        Console.WriteLine("Waiting for your local approval in the open phone app…");
        await phone.PairAsync(Environment.MachineName, timeout.Token);
        settings.Settings.PhonePins[serial] = phone.PeerFingerprint;
        settings.Save();
        Console.WriteLine("Pairing confirmed on the phone and pinned on this desktop.");
        return 0;
    }
    if (!phone.IsPaired) throw new IOException("Pair both devices before running authenticator diagnostics.");
    if (command == "get-info")
    {
        var info = await phone.ExchangeAsync([4], timeout.Token);
        if (info[0] != 0) throw new IOException("GetInfo failed: " + info[0].ToString("X2"));
        Console.WriteLine("Real phone CTAP GetInfo response: " + Convert.ToHexString(info));
        return 0;
    }
    if (command == "phone-test")
    {
        Console.WriteLine("This test creates a REAL credential for keybridge.invalid. Approve registration and assertion fingerprints on your phone.");
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        byte[] user = RandomNumberGenerator.GetBytes(16);
        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(5);
        writer.WriteInt32(1); writer.WriteByteString(challenge);
        writer.WriteInt32(2); writer.WriteStartMap(1); writer.WriteTextString("id"); writer.WriteTextString("keybridge.invalid"); writer.WriteEndMap();
        writer.WriteInt32(3); writer.WriteStartMap(3);
        writer.WriteTextString("id"); writer.WriteByteString(user);
        writer.WriteTextString("name"); writer.WriteTextString("KeyBridge USB self-test");
        writer.WriteTextString("displayName"); writer.WriteTextString("Diagnostic credential"); writer.WriteEndMap();
        writer.WriteInt32(4); writer.WriteStartArray(1); writer.WriteStartMap(2);
        writer.WriteTextString("alg"); writer.WriteInt32(-7); writer.WriteTextString("type"); writer.WriteTextString("public-key");
        writer.WriteEndMap(); writer.WriteEndArray();
        writer.WriteInt32(7); writer.WriteStartMap(2); writer.WriteTextString("rk"); writer.WriteBoolean(true); writer.WriteTextString("uv"); writer.WriteBoolean(true); writer.WriteEndMap(); writer.WriteEndMap();
        var created = await phone.ExchangeAsync(new byte[] { 1 }.Concat(writer.Encode()).ToArray(), timeout.Token);
        RequireSuccess(created, "registration");
        var reader = new CborReader(created.AsMemory(1), CborConformanceMode.Ctap2Canonical);
        byte[]? authData = null;
        reader.ReadStartMap();
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            int key = reader.ReadInt32();
            if (key == 1) { if (reader.ReadTextString() != "none") throw new IOException("Unexpected attestation format."); }
            else if (key == 2) authData = reader.ReadByteString();
            else reader.SkipValue();
        }
        reader.ReadEndMap();
        if (authData is null || authData.Length < 55 || authData[32] != 0x45 || !authData.AsSpan(0, 32).SequenceEqual(SHA256.HashData("keybridge.invalid"u8))) throw new IOException("Invalid registration authenticator data.");
        int idLength = BinaryPrimitives.ReadUInt16BigEndian(authData.AsSpan(53));
        if (idLength is < 1 or > 1024 || authData.Length <= 55 + idLength) throw new IOException("Invalid attested credential ID.");
        byte[] id = authData.AsSpan(55, idLength).ToArray();
        var cose = new CborReader(authData.AsMemory(55 + idLength), CborConformanceMode.Ctap2Canonical);
        byte[]? x = null, y = null;
        int? kty = null, alg = null, curve = null;
        cose.ReadStartMap();
        while (cose.PeekState() != CborReaderState.EndMap)
        {
            int key = cose.ReadInt32();
            switch (key) { case 1: kty = cose.ReadInt32(); break; case 3: alg = cose.ReadInt32(); break; case -1: curve = cose.ReadInt32(); break; case -2: x = cose.ReadByteString(); break; case -3: y = cose.ReadByteString(); break; default: cose.SkipValue(); break; }
        }
        cose.ReadEndMap();
        if (kty != 2 || alg != -7 || curve != 1 || x?.Length != 32 || y?.Length != 32) throw new IOException("Invalid ES256 COSE public key.");
        Console.WriteLine("Registration returned a valid ES256 public key, UP/UV/AT flags and none attestation. Approve the assertion now.");
        writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(4);
        writer.WriteInt32(1); writer.WriteTextString("keybridge.invalid");
        writer.WriteInt32(2); writer.WriteByteString(challenge);
        writer.WriteInt32(3); writer.WriteStartArray(1); writer.WriteStartMap(2); writer.WriteTextString("id"); writer.WriteByteString(id); writer.WriteTextString("type"); writer.WriteTextString("public-key"); writer.WriteEndMap(); writer.WriteEndArray();
        writer.WriteInt32(5); writer.WriteStartMap(1); writer.WriteTextString("uv"); writer.WriteBoolean(true); writer.WriteEndMap(); writer.WriteEndMap();
        var assertion = await phone.ExchangeAsync(new byte[] { 2 }.Concat(writer.Encode()).ToArray(), timeout.Token);
        RequireSuccess(assertion, "assertion");
        reader = new CborReader(assertion.AsMemory(1), CborConformanceMode.Ctap2Canonical);
        byte[]? assertionData = null, signature = null;
        reader.ReadStartMap();
        while (reader.PeekState() != CborReaderState.EndMap)
        { int key = reader.ReadInt32(); if (key == 2) assertionData = reader.ReadByteString(); else if (key == 3) signature = reader.ReadByteString(); else reader.SkipValue(); }
        reader.ReadEndMap();
        if (assertionData?.Length != 37 || signature is null || assertionData[32] != 5 || !assertionData.AsSpan(0,32).SequenceEqual(SHA256.HashData("keybridge.invalid"u8))) throw new IOException("Invalid assertion authenticator data.");
        uint counter = BinaryPrimitives.ReadUInt32BigEndian(assertionData.AsSpan(33));
        if (counter == 0) throw new IOException("The persistent assertion counter did not advance.");
        using var publicKey = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
        bool verified = publicKey.VerifyData(assertionData.Concat(challenge).ToArray(), signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        if (!verified) throw new IOException("The phone assertion signature did not verify.");
        Console.WriteLine($"VERIFIED: Android assertion signature, RP hash, UV/UP flags and counter {counter}. This did not test Windows HID/WebAuthn enumeration.");
        Console.WriteLine("Delete the keybridge.invalid diagnostic credential in the phone vault when finished.");
        return 0;
    }
    Help(); return 2;
}
catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
finally { if (port is int allocated && serial is not null) await adb.RemoveForwardAsync(serial, allocated); }

string? Option(string flag) { int index = Array.IndexOf(args, flag); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
static void RequireSuccess(byte[] response, string operation) { if (response.Length < 2 || response[0] != 0) throw new IOException($"Phone {operation} failed, CTAP status {(response.Length == 0 ? "missing" : response[0].ToString("X2"))}."); }
static void Help() => Console.WriteLine("KeyBridge.Tool devices | hello | pair --code FULL_MATCHING_FINGERPRINT | get-info | phone-test [--serial SERIAL] [--wireless]\nKeyBridge.Tool enable-wireless | disable-wireless\nKeyBridge.Tool bridge [--wireless] [--attach --driver-consent --usbip SIGNED_USBIP_PATH]\nphone-test creates a real diagnostic credential and requires two fingerprint approvals. No driver is installed by this tool.");
