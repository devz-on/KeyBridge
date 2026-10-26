using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using KeyBridge.Usb;

namespace KeyBridge.Core;

public sealed record BridgeSnapshot(string State, string Detail, IReadOnlyList<AdbDevice> Devices,
    string? SelectedSerial, string? DeviceName, string? PairingCode, bool Paired, int CredentialCount,
    string DriverStatus, bool VirtualServerRunning, IReadOnlyList<string> Activity,
    bool DriverInstalled = false, bool DriverSigned = false, bool ClientVerified = false,
    bool? PhoneBiometricReady = null, string? PhoneSecurity = null);

/// <summary>Per-user background bridge shared by the desktop pages and tray.</summary>
public sealed class BridgeController : IAsyncDisposable
{
    private readonly SettingsStore settings = new();
    private readonly AdbManager adb = new();
    private readonly DriverManager driver = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<string> activity = [];
    private readonly Task monitor;
    private PhoneClient? phone;
    private CancellationTokenSource? sessionLifetime;
    private X509Certificate2? identity;
    private UsbIpServer? server;
    private CancellationTokenSource? serverLifetime;
    private Task? serverTask;
    private int? forwardPort;
    private int refreshActive;
    private int forceHealthCheck = 1;
    private bool clientVerified;
    private bool? phoneBiometricReady;
    private string? phoneSecurity;
    private DriverHealth health = new(false, false, "Not checked");
    private string? selectedSerial;
    private string? deviceName;
    private string state = "Disconnected";
    private string detail = "Connect your phone with a USB data cable, then enable USB debugging.";
    private int credentialCount;
    private IReadOnlyList<AdbDevice> devices = [];
    private bool userStopped = true;
    private DateTimeOffset lastDriverCheck;
    public event Action<BridgeSnapshot>? Changed;
    public BridgeSnapshot Snapshot { get; private set; } = new("Disconnected", "Connect a USB phone to begin.", [], null, null, null, false, 0, "Not checked", false, []);
    public string AdbPath { get => settings.Settings.AdbPath; set { settings.Settings.AdbPath = value; adb.Executable = value; settings.Save(); } }
    public string UsbIpPath { get => settings.Settings.UsbIpPath; set { settings.Settings.UsbIpPath = value; clientVerified = false; Interlocked.Exchange(ref forceHealthCheck, 1); settings.Save(); } }
    public bool DriverConsent { get; set; } = true;
    public bool AutoReconnect { get => settings.Settings.AutoReconnect; set { settings.Settings.AutoReconnect = value; settings.Save(); } }
    public bool AllowWirelessDebugging { get => settings.Settings.AllowWirelessDebugging; set { settings.Settings.AllowWirelessDebugging = value; adb.AllowWirelessDebugging = value; settings.Save(); } }
    public BridgeController()
    {
        adb.Executable = settings.Settings.AdbPath;
        adb.AllowWirelessDebugging = settings.Settings.AllowWirelessDebugging;
        monitor = MonitorAsync();
    }
    public Task RefreshChecksAsync() { Interlocked.Exchange(ref forceHealthCheck, 1); return RefreshAsync(); }
    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref refreshActive, 1) != 0) return;
        if (!await lifecycle.WaitAsync(0)) { Interlocked.Exchange(ref refreshActive, 0); return; }
        try
        {
            devices = await adb.DiscoverAsync(lifetime.Token);
            if (Interlocked.Exchange(ref forceHealthCheck, 0) != 0 || DateTimeOffset.UtcNow - lastDriverCheck > TimeSpan.FromSeconds(30))
            {
                health = await driver.InspectAsync(lifetime.Token);
                clientVerified = false;
                if (File.Exists(UsbIpPath))
                {
                    try { await driver.ValidateClientAsync(UsbIpPath, lifetime.Token); clientVerified = true; }
                    catch (Exception ex) when (ex is IOException or JsonException or System.ComponentModel.Win32Exception) { }
                }
                lastDriverCheck = DateTimeOffset.UtcNow;
            }
            if (phone is not null && (!phone.IsConnected || !devices.Any(d => d.Serial == selectedSerial && d.State == "device")))
            {
                await DisconnectInternalAsync();
                SetState("Disconnected", "The phone's ADB connection closed. Authentication has stopped.");
            }
            if (phone is not null && phone.IsPaired)
            {
                var status = await phone.StatusAsync(sessionLifetime?.Token ?? lifetime.Token);
                credentialCount = PhoneClient.GetInt(status, "credentialCount");
                phoneBiometricReady = PhoneClient.GetOptionalBool(status, "biometricAvailable");
                phoneSecurity = PhoneClient.GetString(status, "hardwareSecurity", "Unknown");
            }
            if (phone is null)
            {
                detail = devices.Count == 0 ? (AllowWirelessDebugging ? "No ADB devices found. Reconnect authorized wireless debugging or a USB data cable, then open KeyBridge on the phone." : "No USB ADB devices found. Connect a data cable and open KeyBridge on your phone.")
                    : devices.All(d => d.State == "unauthorized") ? "Unlock your phone and accept the USB debugging authorization dialog."
                    : devices.Count > 1 ? "Choose the intended phone in Devices before connecting."
                    : "Select your phone and connect. Start the KeyBridge service on the phone first.";
                if (userStopped) state = devices.Any(d => d.State == "unauthorized") ? "ADB authorization required" : "Disconnected";
            }
            Publish();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException or System.Security.Authentication.AuthenticationException or JsonException)
        {
            if (!lifetime.IsCancellationRequested)
            {
                if (phone is not null)
                {
                    await DisconnectInternalAsync();
                }
                SetState("Connection error", ex is System.ComponentModel.Win32Exception ? "ADB was not found. Set the Platform Tools path in Settings." : SafeError(ex));
            }
        }
        finally { Interlocked.Exchange(ref refreshActive, 0); lifecycle.Release(); }
    }
    public async Task ConnectAsync(string serial)
    {
        await lifecycle.WaitAsync(lifetime.Token);
        try
        {
            await DisconnectInternalAsync();
            devices = await adb.DiscoverAsync(lifetime.Token);
            if (!devices.Any(d => d.Serial == serial && d.State == "device")) throw new IOException("Select an authorized online phone using an enabled ADB transport.");
            selectedSerial = serial;
            userStopped = false;
            sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            SetState("Connecting", "Opening an authenticated channel to KeyBridge on the phone…");
            forwardPort = await adb.ForwardAsync(serial, sessionLifetime.Token);
            identity ??= HostIdentity.LoadOrCreate();
            phone = new PhoneClient(identity);
            string? expectedPin = settings.ExpectedPhonePin(serial);
            var hello = await phone.ConnectAsync(forwardPort.Value, expectedPin, Environment.MachineName, sessionLifetime.Token);
            deviceName = hello.DeviceName;
            credentialCount = hello.CredentialCount;
            phoneBiometricReady = hello.BiometricAvailable;
            phoneSecurity = hello.HardwareSecurity;
            settings.Settings.LastSerial = serial;
            if (hello.Paired) settings.Settings.PhonePins[serial] = phone.PeerFingerprint;
            settings.Save();
            SetState(hello.Paired ? "Connected" : "Pairing", hello.Paired
                ? (AdbManager.IsWirelessSerial(serial) ? "Wireless ADB · phone identity verified. Every credential operation still requires Android approval." : "USB ADB · phone identity verified. Every credential operation still requires Android approval.")
                : "Compare all 64 characters of the pairing fingerprint on both devices. Confirm it here and approve pairing on the phone.");
        }
        catch
        {
            await DisconnectInternalAsync();
            SetState("Connection error", "Could not connect securely. Open KeyBridge on the phone, start its service, and retry.");
            throw;
        }
        finally { lifecycle.Release(); }
    }
    public async Task ConfirmPairingAsync()
    {
        await lifecycle.WaitAsync(lifetime.Token);
        try
        {
            var current = phone ?? throw new IOException("Connect a phone first.");
            SetState("Pairing", "Approve the matching code in the open KeyBridge phone app. Pairing expires after two minutes.");
            await current.PairAsync(Environment.MachineName, sessionLifetime?.Token ?? lifetime.Token);
            settings.Settings.PhonePins[selectedSerial!] = current.PeerFingerprint;
            settings.Save();
            SetState("Connected", "Both devices are paired. Android approval remains mandatory for every credential operation.");
        }
        finally { lifecycle.Release(); }
    }
    public async Task StartVirtualAsync()
    {
        await lifecycle.WaitAsync(lifetime.Token);
        try
        {
            if (phone?.IsPaired != true) throw new IOException("Pair the phone before starting the virtual authenticator.");
            if (serverTask is not null && !serverTask.IsCompleted) return;
            serverLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            server = new UsbIpServer(new ObservedTransport(phone, OnOperation));
            serverTask = RunServerAsync(server, serverLifetime.Token);
            await Task.Delay(120, lifetime.Token);
            if (serverTask.IsCompleted)
            {
                await server.DisposeAsync();
                server = null; serverTask = null;
                serverLifetime.Dispose(); serverLifetime = null;
                throw new IOException("The USB/IP server could not start. Another process may already be using loopback port 3240.");
            }
            SetState("USB/IP listening", "The local virtual-device server is running. Windows enumeration has not been verified. Use Attach after driver verification.");
        }
        finally { lifecycle.Release(); }
    }
    private async Task RunServerAsync(UsbIpServer instance, CancellationToken ct)
    {
        try { await instance.StartAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { SetState("Virtual device error", SafeError(ex)); }
    }
    public async Task AttachAsync()
    {
        await lifecycle.WaitAsync(lifetime.Token);
        try
        {
            if (!DriverConsent) throw new IOException("Confirm that you have reviewed and verified the installed USB/IP driver before attaching.");
            if (server is null || serverTask?.IsCompleted != false || phone?.IsPaired != true) throw new IOException("Start the paired phone bridge first.");
            health = await driver.InspectAsync(lifetime.Token);
            if (!health.Installed || !health.WindowsReportsSigned) throw new IOException(health.Description);
            var result = await driver.AttachAsync(UsbIpPath, lifetime.Token);
            if (result.ExitCode != 0) throw new IOException("USB/IP attachment failed. Check driver privileges and use the upstream USB/IP diagnostics.");
            await Task.Delay(1500, lifetime.Token);
            bool enumerated = server.IsImported && await driver.DetectKeyBridgeHidAsync(lifetime.Token);
            SetState(enumerated ? "HID device detected" : "Attachment unverified", enumerated
                ? "Windows reports the KeyBridge HID interface. Browser registration and authentication still require end-to-end testing."
                : "The attach command finished, but KeyBridge HID enumeration could not be confirmed.");
        }
        finally { lifecycle.Release(); }
    }
    public async Task StopAsync()
    {
        sessionLifetime?.Cancel();
        await lifecycle.WaitAsync();
        try { userStopped = true; await DisconnectInternalAsync(); SetState("Disconnected", "Bridge stopped. No credential requests can reach the phone."); }
        finally { lifecycle.Release(); }
    }
    public async Task ForgetPairingAsync()
    {
        if (selectedSerial is null) throw new IOException("Select a phone whose desktop pairing should be removed.");
        await ForgetPairingAsync(selectedSerial);
    }
    public async Task ForgetPairingAsync(string serial)
    {
        if (serial == selectedSerial) sessionLifetime?.Cancel();
        await lifecycle.WaitAsync();
        try
        {
            if (serial == selectedSerial)
            {
                userStopped = true;
                await DisconnectInternalAsync();
            }
            settings.Settings.PhonePins.Remove(serial);
            settings.Save();
            SetState(phone?.IsPaired == true ? state : "Disconnected", "Desktop pairing removed. Revoke this computer in Android KeyBridge to remove its phone authorization too.");
        }
        finally { lifecycle.Release(); }
    }
    public async Task InstallAndroidAsync(string apkPath)
    {
        var available = await adb.DiscoverAsync(lifetime.Token);
        string? serial = selectedSerial ?? (available.Count(d => d.State == "device") == 1 ? available.First(d => d.State == "device").Serial : null);
        if (serial is null) throw new IOException("Select one authorized phone before installing the Android app.");
        await InstallAndroidAsync(serial, apkPath);
    }
    public async Task InstallAndroidAsync(string serial, string apkPath)
    {
        if (!(await adb.DiscoverAsync(lifetime.Token)).Any(d => d.Serial == serial && d.State == "device"))
            throw new IOException("Select an authorized phone before installing the Android app.");
        if (!await adb.VerifyTransportAsync(serial, lifetime.Token)) throw new IOException("Android installation requires an explicitly enabled authorized ADB transport.");
        await adb.InstallAsync(serial, apkPath, lifetime.Token);
        SetState(state, "Android app installed and launched. Start its bridge service on the phone.");
    }
    public Task ExportDiagnosticsAsync(string path)
    {
        // Do not export serials, host/phone names, RP IDs, account handles, paths, certificate pins, or CTAP payloads.
        var report = new { product = "KeyBridge", version = "0.1.0", utc = DateTimeOffset.UtcNow,
            os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(), state,
            usbDeviceCount = devices.Count, deviceStates = devices.Select(d => d.State),
            paired = phone?.IsPaired == true, driverInstalled = health.Installed,
            windowsReportsDriverSigned = health.WindowsReportsSigned, virtualServerListening = server is not null,
            hardwareAcceptance = "Not verified by diagnostic export. See docs/verification.md." };
        return File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
    private async Task DisconnectInternalAsync()
    {
        sessionLifetime?.Cancel();
        serverLifetime?.Cancel();
        if (server is not null) await server.DisposeAsync();
        if (serverTask is not null) { try { await serverTask; } catch (OperationCanceledException) { } }
        server = null; serverTask = null;
        serverLifetime?.Dispose(); serverLifetime = null;
        if (phone is not null) await phone.DisposeAsync();
        phone = null;
        sessionLifetime?.Dispose(); sessionLifetime = null;
        if (forwardPort is int port && selectedSerial is not null) await adb.RemoveForwardAsync(selectedSerial, port);
        forwardPort = null;
        credentialCount = 0;
        deviceName = null;
    }
    private void OnOperation(string operation)
    {
        AddActivity(operation);
        Publish();
    }
    private void SetState(string next, string message)
    {
        if (state != next) AddActivity(next);
        state = next; detail = message; Publish();
    }
    private void AddActivity(string message)
    {
        lock (activity) { activity.Insert(0, $"{DateTimeOffset.Now:t}  {message}"); if (activity.Count > 100) activity.RemoveAt(activity.Count - 1); }
    }
    private void Publish()
    {
        string[] entries;
        lock (activity) entries = activity.ToArray();
        var next = new BridgeSnapshot(state, detail, devices, selectedSerial, deviceName, phone?.PairingCode,
            phone?.IsPaired == true, credentialCount, health.Description, server is not null && serverTask?.IsCompleted == false, entries,
            health.Installed, health.WindowsReportsSigned, clientVerified,
            phone?.IsPaired == true ? phoneBiometricReady : null, phone?.IsPaired == true ? phoneSecurity : null);
        Snapshot = next;
        Changed?.Invoke(next);
    }
    private async Task MonitorAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                // Pairing/attach uses lifecycle exclusively; never contend with approval waits.
                if (lifecycle.CurrentCount == 0) continue;
                await RefreshAsync();
                if (AutoReconnect && !userStopped && phone is null && settings.Settings.LastSerial is string serial &&
                    settings.Settings.PhonePins.ContainsKey(serial) && devices.Any(d => d.Serial == serial && d.State == "device"))
                {
                    try { await ConnectAsync(serial); }
                    catch (Exception ex) when (!lifetime.IsCancellationRequested && ex is IOException or InvalidDataException or OperationCanceledException or System.Security.Authentication.AuthenticationException or System.ComponentModel.Win32Exception) { }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }
    private static string SafeError(Exception exception) => exception switch {
        System.Net.Sockets.SocketException => "The local phone or USB/IP connection is unavailable. Check the cable, forwarding, and phone service.",
        System.Security.Authentication.AuthenticationException => "Phone identity or TLS verification failed. Compare identities and revoke/re-pair locally if the phone was reset.",
        _ => exception.Message.Length > 300 ? "An operation failed. Check local diagnostics." : exception.Message
    };
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        await lifecycle.WaitAsync();
        try { await DisconnectInternalAsync(); }
        finally { lifecycle.Release(); }
        try { await monitor; } catch (OperationCanceledException) { }
        identity?.Dispose();
    }
    private sealed class ObservedTransport(ICtapTransport inner, Action<string> report) : ICtapTransport
    {
        public async Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken)
        {
            report(request.FirstOrDefault() switch { 1 => "Registration requested on phone", 2 => "Authentication requested on phone", 4 => "Authenticator information requested", _ => "CTAP request forwarded" });
            var response = await inner.ExchangeAsync(request, cancellationToken);
            report(response.Length > 0 && response[0] == 0 ? "Phone completed the CTAP operation" : "Phone rejected the CTAP operation: " + (response.Length > 0 ? response[0].ToString("X2") : "empty response"));
            return response;
        }
    }
}
