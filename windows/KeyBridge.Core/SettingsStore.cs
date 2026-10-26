using System.Text.Json;

namespace KeyBridge.Core;

public sealed class BridgeSettings
{
    public string AdbPath { get; set; } = AdbManager.FindAdb();
    public string UsbIpPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip", "usbip.exe");
    public Dictionary<string, string> PhonePins { get; set; } = [];
    public bool AutoReconnect { get; set; }
    public bool AllowWirelessDebugging { get; set; }
    public string? LastSerial { get; set; }
}

public sealed class SettingsStore
{
    public string DirectoryPath { get; }
    public BridgeSettings Settings { get; }
    private string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public SettingsStore(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyBridge");
        Directory.CreateDirectory(DirectoryPath);
        try { Settings = JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException) { Settings = new(); }
        // Setup supplies private Platform Tools beside the app. Preserve valid custom tool paths.
        string packagedAdb = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "platform-tools", "adb.exe"));
        if (!File.Exists(Settings.AdbPath) && File.Exists(packagedAdb)) Settings.AdbPath = packagedAdb;
        string installedUsbIp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip", "usbip.exe");
        if (!File.Exists(Settings.UsbIpPath) && File.Exists(installedUsbIp)) Settings.UsbIpPath = installedUsbIp;
    }
    public void Save()
    {
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, overwrite: true);
    }
    public string? ExpectedPhonePin(string serial)
    {
        if (Settings.PhonePins.TryGetValue(serial, out string? exact)) return exact;
        // ADB can change its routing name between mDNS, IP:port and USB. An existing cryptographic
        // identity can be recognized without enrolling a new phone. Never guess among different pins.
        string[] known = Settings.PhonePins.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return known.Length == 1 ? known[0] : null;
    }
}
