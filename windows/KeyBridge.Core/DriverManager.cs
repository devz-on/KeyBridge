using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace KeyBridge.Core;

public sealed record DriverHealth(bool Installed, bool WindowsReportsSigned, string Description);

/// <summary>Read-only inspection. Installation is an explicit, separate administrator action.</summary>
public sealed class DriverManager
{
    public async Task<DriverHealth> InspectAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return new(false, false, "Windows is required for the VHCI driver.");
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\usbip2_ude");
        if (service is null) return new(false, false, "USB/IP driver is missing. Run the all-in-one KeyBridge installer and select the signed driver component.");
        try
        {
            var result = await RunPowerShellAsync("@(Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DeviceName -match 'USB/IP|USBip' } | Select-Object DeviceName,IsSigned,DriverVersion) | ConvertTo-Json -Compress", ct);
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(result.Output) ? "[]" : result.Output);
            var rows = json.RootElement.ValueKind == JsonValueKind.Array ? json.RootElement.EnumerateArray().ToArray() : [json.RootElement];
            bool signed = rows.Length > 0 && rows.All(row => row.TryGetProperty("IsSigned", out var flag) && flag.ValueKind == JsonValueKind.True);
            return new(true, signed, signed
                ? "Windows reports a signed USB/IP driver. Compatibility and KeyBridge enumeration still require testing."
                : "USB/IP service exists; a signed working device could not be verified. Attachment is blocked.");
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException)
        { return new(true, false, "USB/IP service exists, but driver signature/health inspection failed. Attachment is blocked."); }
    }
    public async Task ValidateClientAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path) || !string.Equals(Path.GetFileName(path), "usbip.exe", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Select usbip.exe from a verified upstream installation.");
        // The value is transferred through an environment variable, never interpolated into script code.
        var result = await RunPowerShellAsync("$ErrorActionPreference='Stop'; $s=Get-AuthenticodeSignature -LiteralPath $env:KEYBRIDGE_VERIFY_PATH; @{Valid=($s.Status -eq 'Valid'); Publisher=$s.SignerCertificate.Subject; Thumbprint=$s.SignerCertificate.Thumbprint; SignatureStatus=[string]$s.Status} | ConvertTo-Json -Compress", ct, path);
        using var json = JsonDocument.Parse(result.Output);
        bool valid = json.RootElement.TryGetProperty("Valid", out var flag) && flag.GetBoolean();
        string publisher = PhoneClient.GetString(json.RootElement, "Publisher", "");
        string thumbprint = PhoneClient.GetString(json.RootElement, "Thumbprint", "");
        bool knownPublisher = publisher.Contains("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase) ||
            thumbprint.Equals("9AC56B6C76141395D74FFF6652818376E80B9C95", StringComparison.OrdinalIgnoreCase);
        if (!valid || !knownPublisher)
            throw new IOException($"USB/IP client signature verification failed (status={PhoneClient.GetString(json.RootElement, "SignatureStatus", "unknown")}, verifiedPublisher={knownPublisher}). Check the signed client path and docs/driver-verification.md.");
    }
    public async Task<CommandResult> AttachAsync(string executable, CancellationToken ct = default)
    {
        await ValidateClientAsync(executable, ct);
        return await RunClientAsync(executable, ["attach", "-r", "127.0.0.1", "-b", "1-1"], ct);
    }
    public async Task<bool> DetectKeyBridgeHidAsync(CancellationToken ct = default)
    {
        // Filter by both the development descriptor hardware ID and the FIDO HID usage.
        var result = await RunPowerShellAsync("$found=@(Get-PnpDevice -PresentOnly -Class HIDClass -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'OK' -and $_.InstanceId -match 'VID_1209&PID_0001' } | Where-Object { $ids=@((Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName DEVPKEY_Device_HardwareIds -ErrorAction SilentlyContinue).Data)+@((Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName DEVPKEY_Device_CompatibleIds -ErrorAction SilentlyContinue).Data); @($ids | Where-Object { $_ -match 'HID_DEVICE_UP:F1D0_U:0001' }).Count -gt 0 }); [bool]($found.Count -gt 0) | ConvertTo-Json -Compress", ct);
        return result.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }
    private static async Task<CommandResult> RunClientAsync(string executable, IEnumerable<string> args, CancellationToken ct)
    {
        var command = new AdbManager { Executable = executable };
        return await command.RunAsync(args, ct, 20);
    }
    private static async Task<CommandResult> RunPowerShellAsync(string script, CancellationToken ct, string? verificationPath = null)
    {
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // Do not inherit PowerShell 7 binary-module paths into Windows PowerShell 5.1.
        info.Environment["PSModulePath"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "Modules");
        foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script }) info.ArgumentList.Add(arg);
        if (verificationPath is not null) info.Environment["KEYBRIDGE_VERIFY_PATH"] = Path.GetFullPath(verificationPath);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var process = Process.Start(info) ?? throw new IOException("Could not start driver inspection.");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        var result = new CommandResult(process.ExitCode, await output, await error);
        if (result.ExitCode != 0) throw new IOException("Windows driver inspection failed.");
        return result;
    }
}
