using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace KeyBridge.Core;

public sealed record AdbDevice(string Serial, string State, string Model)
{
    public bool IsWireless => AdbManager.IsWirelessSerial(Serial);
}
public sealed record CommandResult(int ExitCode, string Output, string Error);

public class AdbManager
{
    public string Executable { get; set; } = FindAdb();
    public bool AllowWirelessDebugging { get; set; }
    public static bool IsWirelessSerial(string serial) => serial.Contains(':') || serial.Contains("._adb-tls-connect._tcp", StringComparison.Ordinal) || serial.Contains("._adb._tcp", StringComparison.Ordinal);
    public static string FindAdb()
    {
        string[] candidates = [
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "platform-tools", "adb.exe")),
            Path.Combine(Environment.GetEnvironmentVariable("ANDROID_HOME") ?? "", "platform-tools", "adb.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT") ?? "", "platform-tools", "adb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe")];
        return candidates.FirstOrDefault(File.Exists) ?? "adb.exe";
    }
    public static IReadOnlyList<AdbDevice> ParseDevices(string output) => output.Split('\n')
        .Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(parts => parts.Length >= 2 && !parts[0].StartsWith('*') && parts[0] != "List" &&
            parts[1] is "device" or "unauthorized" or "offline" or "recovery" or "sideload")
        .Select(parts => new AdbDevice(parts[0], parts[1],
            parts.FirstOrDefault(s => s.StartsWith("model:", StringComparison.Ordinal))?[6..].Replace('_', ' ') ?? "Android device"))
        .ToArray();

    public async Task<IReadOnlyList<AdbDevice>> DiscoverAsync(CancellationToken ct = default)
    {
        var result = await RunAsync(["devices", "-l"], ct);
        EnsureSuccess(result, "ADB discovery failed");
        return ParseDevices(result.Output).Where(d => (AllowWirelessDebugging || !d.IsWireless) && !d.Serial.StartsWith("emulator-", StringComparison.Ordinal)).ToArray();
    }
    public async Task<int> ForwardAsync(string serial, CancellationToken ct = default)
    {
        if (!await VerifyTransportAsync(serial, ct))
            throw new IOException("The selected ADB transport is not allowed. Connect USB or explicitly enable already-authorized wireless debugging.");
        var result = await RunAsync(["-s", serial, "forward", "tcp:0", "tcp:37123"], ct);
        EnsureSuccess(result, "Could not create the local ADB forward");
        if (!int.TryParse(result.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1024 or > 65535)
            throw new IOException("ADB returned an invalid local forwarding port.");
        return port;
    }
    public async Task<bool> VerifyTransportAsync(string serial, CancellationToken ct = default)
    {
        if (IsWirelessSerial(serial))
            return AllowWirelessDebugging && (await DiscoverAsync(ct)).Any(d => d.Serial == serial && d.State == "device");
        return await VerifyUsbAsync(serial, ct);
    }
    public async Task<bool> VerifyUsbAsync(string serial, CancellationToken ct = default)
    {
        var path = await RunAsync(["-s", serial, "get-devpath"], ct);
        EnsureSuccess(path, "USB device verification failed");
        string devicePath = path.Output.Trim();
        if (devicePath.StartsWith("usb:", StringComparison.Ordinal)) return true;
        if (devicePath != "unknown" || serial.Contains(':') || serial.StartsWith("emulator-", StringComparison.Ordinal)) return false;
        // Windows Platform Tools can report "unknown" for genuine USB paths. -d is ADB's explicit USB selector.
        var onlyUsb = await RunAsync(["-d", "get-serialno"], ct);
        if (onlyUsb.ExitCode == 0 && string.Equals(onlyUsb.Output.Trim(), serial, StringComparison.Ordinal)) return true;
        if (!OperatingSystem.IsWindows()) return false;
        // With multiple USB phones, verify the selected serial against a present USB PnP device.
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var inspector = new AdbManager { Executable = executable };
        var pnp = await inspector.RunAsync(["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "@(Get-PnpDevice -PresentOnly -ErrorAction Stop | Where-Object { $_.InstanceId.StartsWith('USB\\', [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { $_.InstanceId }) | ConvertTo-Json -Compress"], ct, 20);
        if (pnp.ExitCode != 0 || string.IsNullOrWhiteSpace(pnp.Output)) return false;
        using var json = JsonDocument.Parse(pnp.Output);
        var rows = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray().ToArray() : [json.RootElement];
        return rows.Any(row => row.ValueKind == JsonValueKind.String && row.GetString()!.EndsWith("\\" + serial, StringComparison.OrdinalIgnoreCase));
    }
    public async Task RemoveForwardAsync(string serial, int port)
    {
        try { await RunAsync(["-s", serial, "forward", "--remove", $"tcp:{port}"], CancellationToken.None); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or OperationCanceledException) { }
    }
    public async Task InstallAsync(string serial, string apkPath, CancellationToken ct = default)
    {
        if (!File.Exists(apkPath) || Path.GetExtension(apkPath) != ".apk") throw new FileNotFoundException("Select a built KeyBridge APK.", apkPath);
        EnsureSuccess(await RunAsync(["-s", serial, "install", "-r", Path.GetFullPath(apkPath)], ct, 120), "APK installation failed");
        EnsureSuccess(await RunAsync(["-s", serial, "shell", "am", "start", "-n", "dev.keybridge.android/.MainActivity"], ct), "Could not launch KeyBridge");
    }
    public virtual async Task<CommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken ct = default, int timeoutSeconds = 15)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileName(Executable).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase))
            info.Environment["PSModulePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "Modules");
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return new(process.ExitCode, await stdout, await stderr);
        }
        catch { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } throw; }
    }
    private static void EnsureSuccess(CommandResult result, string message)
    {
        if (result.ExitCode != 0) throw new IOException($"{message}. {result.Error.Trim()}");
    }
}
