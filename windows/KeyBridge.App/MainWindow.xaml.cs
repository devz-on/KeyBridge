using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using KeyBridge.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace KeyBridge.App;

public partial class MainWindow : Window
{
    private readonly BridgeController _bridge = new();
    private readonly Dictionary<string, Button> _navigation = new();
    private readonly UiPreferences _preferences;
    private readonly Forms.NotifyIcon _tray;
    private string _page = "Connection";
    private string? _selectedSerial;
    private bool _busy;
    private bool _exiting;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private string _lastState = "";
    private string _lastRenderedStamp = "";
    private static string PreferencePath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyBridge", "ui.json");

    public MainWindow()
    {
        InitializeComponent();
        _preferences = LoadPreferences();
        ApplyTheme();
        string[] titles = ["Connection", "Checks", "Activity", "Settings"];
        string[] glyphs = ["\uE8EA", "\uE73A", "\uE81C", "\uE713"];
        for (var i = 0; i < titles.Length; i++)
        {
            var name = titles[i];
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = glyphs[i], FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 17, Foreground = Brushes.White, Width = 31, VerticalAlignment = VerticalAlignment.Center });
            content.Children.Add(new TextBlock { Text = name, Foreground = new SolidColorBrush(Color.FromRgb(222, 216, 242)), VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = content, Style = (Style)FindResource("NavButton") };
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => Navigate(name);
            _navigation.Add(name, button);
            Navigation.Children.Add(button);
        }
        _bridge.Changed += OnBridgeChanged;
        _tray = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/KeyBridge.ico")).Stream), Text = "KeyBridge", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open KeyBridge", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("Stop bridge", null, (_, _) => Dispatcher.InvokeAsync(StopConnectionAsync));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(() => { _exiting = true; Close(); }));
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
        Closing += WindowClosing;
        Loaded += async (_, _) =>
        {
            Navigate("Connection");
            if (Environment.GetCommandLineArgs().Contains("--background") && _preferences.KeepInTray) Hide();
            await RunAction(_bridge.RefreshAsync);
        };
    }

    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete) return;
        if (!_exiting && _preferences.KeepInTray)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        e.Cancel = true;
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        _exiting = true;
        IsEnabled = false;
        try { await _bridge.DisposeAsync(); }
        catch (Exception ex) { Debug.WriteLine(ex.GetType().Name); }
        _tray.Visible = false;
        _tray.Dispose();
        _shutdownComplete = true;
        Close();
    }

    private void OnBridgeChanged(BridgeSnapshot state)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnBridgeChanged(state)); return; }
        ShellStatus.Text = state.State;
        if (_preferences.Notifications && _lastState.Length > 0 && state.State != _lastState && !IsActive)
            _tray.ShowBalloonTip(3000, "KeyBridge", state.State, Forms.ToolTipIcon.Info);
        _lastState = state.State;
        // Preserve fields and selection while an operation is pending.
        if (!_busy && !_exiting && _page != "Settings" && SnapshotStamp(state) != _lastRenderedStamp) RenderPage();
    }

    private void Navigate(string page)
    {
        _page = page;
        string section = page is "Diagnostics" or "About" ? "Settings" : page;
        foreach (var item in _navigation) item.Value.Background = item.Key == section ? new SolidColorBrush(Color.FromRgb(70, 61, 102)) : Brushes.Transparent;
        RenderPage();
        if (!_preferences.ReducedMotion && SystemParameters.ClientAreaAnimation)
        {
            PageContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(180)));
        }
    }

    private void RenderPage()
    {
        var state = _bridge.Snapshot;
        _lastRenderedStamp = SnapshotStamp(state);
        ShellStatus.Text = state.State;
        PageContent.Content = _page switch
        {
            "Activity" => BuildActivity(state),
            "Checks" => BuildChecks(state),
            "Settings" => BuildSettings(),
            "Diagnostics" => BuildDiagnostics(state),
            "About" => BuildAbout(),
            _ => BuildConnection(state)
        };
    }

    private StackPanel Page(string title, string subtitle)
    {
        var panel = new StackPanel();
        if (_page is "Diagnostics" or "About")
            panel.Children.Add(Action("←  Settings", () => { Navigate("Settings"); return Task.CompletedTask; }, true));
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("PageTitle") });
        if (subtitle.Length > 0) panel.Children.Add(new TextBlock { Text = subtitle, Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 0, 0, 20) });
        return panel;
    }

    private StackPanel BuildConnection(BridgeSnapshot state)
    {
        var page = Page("Connection", "");
        Button? connectButton = null;
        Button? installButton = null;
        Button? forgetButton = null;
        var list = new StackPanel();
        list.Children.Add(SectionTitle("Phone"));
        if (state.Devices.Count == 0)
        {
            var empty = new StackPanel { Margin = new Thickness(0, 24, 0, 18) };
            empty.Children.Add(new TextBlock { Text = "\uE8EA", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 40, Foreground = Brush("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            empty.Children.Add(new TextBlock { Text = "No phone connected", FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 4) });
            empty.Children.Add(new TextBlock { Text = "Use a USB data cable, then refresh.", Foreground = Brush("MutedBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            list.Children.Add(empty);
        }
        else
        {
            var choices = state.Devices.Select(device => new DeviceChoice(device.Serial, $"{device.Model}  ·  {(device.IsWireless ? "Wireless" : "USB")}  ·  {device.State}")).ToArray();
            var picker = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Display" };
            picker.SelectedItem = choices.FirstOrDefault(x => x.Serial == (_selectedSerial ?? state.SelectedSerial)) ?? choices.First();
            _selectedSerial = (picker.SelectedItem as DeviceChoice)?.Serial;
            picker.SelectionChanged += (_, _) =>
            {
                _selectedSerial = (picker.SelectedItem as DeviceChoice)?.Serial;
                bool online = !_busy && state.Devices.Any(device => device.Serial == _selectedSerial && device.State == "device");
                if (connectButton is not null) connectButton.IsEnabled = online;
                if (installButton is not null) installButton.IsEnabled = online;
                if (forgetButton is not null) forgetButton.IsEnabled = !_busy && (_selectedSerial is not null || state.SelectedSerial is not null);
            };
            AutomationProperties.SetName(picker, "Android phone");
            list.Children.Add(picker);
        }
        var actions = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        connectButton = Action("Connect", async () =>
        {
            if (_selectedSerial is null) throw new InvalidOperationException("Select an authorized Android device first.");
            await _bridge.ConnectAsync(_selectedSerial);
        }, false, state.Devices.Any(x => x.Serial == _selectedSerial && x.State == "device"));
        actions.Children.Add(connectButton);
        installButton = Action("Install phone app", InstallAndroid, true, state.Devices.Any(x => x.Serial == _selectedSerial && x.State == "device"));
        actions.Children.Add(installButton);
        list.Children.Add(actions);
        page.Children.Add(Card(list));

        var pairing = new StackPanel();
        pairing.Children.Add(SectionTitle("Pairing"));
        if (!state.Paired && !string.IsNullOrWhiteSpace(state.PairingCode)) pairing.Children.Add(Muted("Compare all 64 fingerprint characters on both screens, then approve pairing on your phone."));
        if (!state.Paired && !string.IsNullOrWhiteSpace(state.PairingCode))
        {
            pairing.Children.Add(new TextBlock { Text = GroupCode(state.PairingCode), FontSize = 22, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontWeight = FontWeights.SemiBold, Foreground = Brush("AccentBrush"), Margin = new Thickness(0, 20, 0, 20) });
        }
        else pairing.Children.Add(new TextBlock { Text = state.Paired ? "Paired · " + state.DeviceName : "Connect your phone to pair.", Margin = new Thickness(0, 0, 0, 16), Foreground = Brush("MutedBrush") });
        var pairActions = new WrapPanel();
        if (!state.Paired && !string.IsNullOrWhiteSpace(state.PairingCode)) pairActions.Children.Add(Action("Codes match · pair", async () =>
        {
            if (MessageBox.Show(this, $"The phone must display {GroupCode(_bridge.Snapshot.PairingCode ?? "")}.\n\nConfirm that both codes match, then approve pairing in the Android app.", "Confirm phone identity", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            await _bridge.ConfirmPairingAsync();
        }, false, !state.Paired && !string.IsNullOrWhiteSpace(state.PairingCode)));
        forgetButton = Action("Forget phone", ForgetPairing, true, _selectedSerial is not null || state.SelectedSerial is not null);
        pairActions.Children.Add(forgetButton);
        pairActions.Children.Add(Action("Disconnect", _bridge.StopAsync, true, state.SelectedSerial is not null));
        if (!state.Paired && !string.IsNullOrWhiteSpace(state.PairingCode))
        {
            var cancel = new Button { Content = "Cancel pairing", Style = (Style)FindResource("Secondary") };
            // Cancellation must remain available while a phone approval is pending.
            cancel.Click += async (_, _) => await StopConnectionAsync();
            pairActions.Children.Add(cancel);
        }
        pairing.Children.Add(pairActions);
        page.Children.Add(Card(pairing));
        var key = new StackPanel();
        key.Children.Add(SectionTitle("Windows security key"));
        key.Children.Add(StatusRow("Status", state.State == "HID device detected" ? "Ready for passkey requests" : state.VirtualServerRunning ? "Backend running; Windows attachment needs attention" : "Not enabled", state.State == "HID device detected"));
        var consent = new CheckBox { Content = "Allow signed driver attachment", IsChecked = _bridge.DriverConsent };
        consent.Checked += (_, _) => { _bridge.DriverConsent = true; RenderPage(); };
        consent.Unchecked += (_, _) => { _bridge.DriverConsent = false; RenderPage(); };
        key.Children.Add(consent);
        var keyActions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        keyActions.Children.Add(Action("Enable security key", async () =>
        {
            await _bridge.StartVirtualAsync();
            await _bridge.AttachAsync();
        }, false, state.Paired && _bridge.DriverConsent && state.State != "HID device detected"));
        keyActions.Children.Add(Action("View checks", () => { Navigate("Checks"); return Task.CompletedTask; }, true));
        key.Children.Add(keyActions);
        page.Children.Add(Card(key));
        return page;
    }

    private StackPanel BuildChecks(BridgeSnapshot state)
    {
        var page = Page("Checks", "");
        bool online = state.Devices.Any(device => device.State == "device");
        var checks = new (string Name, bool Done, string Detail)[]
        {
            ("Windows", OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && Environment.Is64BitProcess, "Windows 11 · x64"),
            ("ADB", File.Exists(_bridge.AdbPath), File.Exists(_bridge.AdbPath) ? "Available" : "Run setup or choose adb.exe in Settings"),
            ("USB/IP driver", state.DriverInstalled && state.DriverSigned, state.DriverInstalled && state.DriverSigned ? "Installed · Windows reports signed" : "Install the driver from KeyBridge setup"),
            ("USB/IP client", state.ClientVerified, state.ClientVerified ? "Signature verified" : "Waiting for a verified usbip.exe"),
            ("Phone connected", online, online ? "Authorized ADB device found" : "Connect USB or authorized wireless debugging"),
            ("Phone app", state.DeviceName is not null, state.DeviceName is not null ? "Bridge reachable" : "Open the phone app, start its bridge, then Connect"),
            ("Pairing", state.Paired, state.Paired ? "Identity verified" : "Compare both fingerprints and approve pairing"),
            ("Phone ready", state.Paired && state.PhoneBiometricReady == true, state.PhoneBiometricReady == true ? "Strong biometric available · " + state.PhoneSecurity : state.PhoneBiometricReady == false ? "Enroll a strong biometric on the phone" : "Connect an updated phone app to check"),
            ("Virtual key", state.VirtualServerRunning, state.VirtualServerRunning ? "Backend running" : "Choose Enable security key in Connection"),
            ("Windows detection", state.State == "HID device detected", state.State == "HID device detected" ? "FIDO HID detected" : "Enable the key to check Windows detection")
        };
        int complete = checks.Count(check => check.Done);
        var progress = new StackPanel();
        progress.Children.Add(SectionTitle(complete == checks.Length ? "Ready to use" : $"{complete} of {checks.Length} complete"));
        progress.Children.Add(new ProgressBar { Value = complete, Maximum = checks.Length, Height = 6 });
        page.Children.Add(Card(progress));
        var items = new StackPanel();
        foreach (var check in checks) items.Children.Add(StatusRow(check.Name, check.Detail, check.Done));
        items.Children.Add(new TextBlock { Text = "Website registration is verified separately in the browser.", Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 20, 0, 16) });
        var actions = new WrapPanel();
        actions.Children.Add(Action("Check again", _bridge.RefreshChecksAsync));
        actions.Children.Add(Action("Connection", () => { Navigate("Connection"); return Task.CompletedTask; }, true));
        items.Children.Add(actions);
        page.Children.Add(Card(items));
        return page;
    }

    private StackPanel BuildActivity(BridgeSnapshot state)
    {
        var page = Page("Activity", "");
        var panel = new StackPanel();
        if (state.Activity.Count == 0)
        {
            panel.Children.Add(Muted("No events yet."));
        }
        foreach (var entry in state.Activity)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 15) };
            row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Brush("AccentBrush"), Margin = new Thickness(0, 7, 13, 0), VerticalAlignment = VerticalAlignment.Top });
            row.Children.Add(new TextBlock { Text = entry, LineHeight = 23 });
            panel.Children.Add(row);
        }
        page.Children.Add(Card(panel));
        return page;
    }

    private StackPanel BuildSettings()
    {
        var page = Page("Settings", "");
        var tools = new StackPanel();
        tools.Children.Add(SectionTitle("Connection tools"));
        tools.Children.Add(new TextBlock { Text = "Android Debug Bridge · adb.exe", Margin = new Thickness(0, 18, 0, 0), FontWeight = FontWeights.SemiBold });
        var adb = new TextBox { Text = _bridge.AdbPath };
        AutomationProperties.SetName(adb, "ADB executable path");
        tools.Children.Add(adb);
        tools.Children.Add(new TextBlock { Text = "USB/IP client · usbip.exe", FontWeight = FontWeights.SemiBold });
        var usbip = new TextBox { Text = _bridge.UsbIpPath };
        AutomationProperties.SetName(usbip, "USB IP executable path");
        tools.Children.Add(usbip);
        var wireless = new CheckBox { Content = "Allow authorized wireless ADB", IsChecked = _bridge.AllowWirelessDebugging };
        wireless.Checked += (_, _) => _bridge.AllowWirelessDebugging = true;
        wireless.Unchecked += async (_, _) => { await RunAction(_bridge.StopAsync); _bridge.AllowWirelessDebugging = false; };
        tools.Children.Add(wireless);
        tools.Children.Add(Action("Save tool paths", () =>
        {
            _bridge.AdbPath = adb.Text.Trim().Trim('"');
            _bridge.UsbIpPath = usbip.Text.Trim().Trim('"');
            return _bridge.RefreshAsync();
        }));
        page.Children.Add(new Expander { Header = "Advanced connection tools", Content = Card(tools), Margin = new Thickness(0, 0, 0, 18) });

        var appearance = new StackPanel();
        appearance.Children.Add(SectionTitle("Appearance"));
        var dark = new CheckBox { Content = "Dark appearance", IsChecked = _preferences.Dark };
        dark.Checked += (_, _) => { _preferences.Dark = true; SavePreferences(); ApplyTheme(); };
        dark.Unchecked += (_, _) => { _preferences.Dark = false; SavePreferences(); ApplyTheme(); };
        appearance.Children.Add(dark);
        var motion = new CheckBox { Content = "Reduce motion", IsChecked = _preferences.ReducedMotion };
        motion.Checked += (_, _) => { _preferences.ReducedMotion = true; SavePreferences(); };
        motion.Unchecked += (_, _) => { _preferences.ReducedMotion = false; SavePreferences(); };
        appearance.Children.Add(motion);
        page.Children.Add(Card(appearance));

        var system = new StackPanel();
        system.Children.Add(SectionTitle("Startup and tray"));
        var tray = new CheckBox { Content = "Minimize to tray on close", IsChecked = _preferences.KeepInTray };
        tray.Checked += (_, _) => { _preferences.KeepInTray = true; SavePreferences(); };
        tray.Unchecked += (_, _) => { _preferences.KeepInTray = false; SavePreferences(); };
        system.Children.Add(tray);
        var notifications = new CheckBox { Content = "Background connection notifications", IsChecked = _preferences.Notifications };
        notifications.Checked += (_, _) => { _preferences.Notifications = true; SavePreferences(); };
        notifications.Unchecked += (_, _) => { _preferences.Notifications = false; SavePreferences(); };
        system.Children.Add(notifications);
        var reconnect = new CheckBox { Content = "Reconnect after connection loss", IsChecked = _bridge.AutoReconnect };
        reconnect.Checked += (_, _) => _bridge.AutoReconnect = true;
        reconnect.Unchecked += (_, _) => _bridge.AutoReconnect = false;
        system.Children.Add(reconnect);
        var startup = new CheckBox { Content = "Start when I sign in", IsChecked = IsStartupEnabled() };
        startup.Checked += (_, _) => SetStartup(true);
        startup.Unchecked += (_, _) => SetStartup(false);
        system.Children.Add(startup);
        page.Children.Add(Card(system));
        var help = new WrapPanel();
        help.Children.Add(Action("Diagnostics", () => { Navigate("Diagnostics"); return Task.CompletedTask; }, true));
        help.Children.Add(Action("About KeyBridge", () => { Navigate("About"); return Task.CompletedTask; }, true));
        page.Children.Add(Card(help));
        return page;
    }

    private StackPanel BuildDiagnostics(BridgeSnapshot state)
    {
        var page = Page("Diagnostics", "");
        var summary = new StackPanel();
        summary.Children.Add(SectionTitle("Live diagnostics"));
        summary.Children.Add(StatusRow("Application", "KeyBridge 0.1.2 · " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture, true));
        summary.Children.Add(StatusRow("Windows", Environment.OSVersion.VersionString, OperatingSystem.IsWindows()));
        summary.Children.Add(StatusRow("ADB tool", File.Exists(_bridge.AdbPath) ? "Executable found" : "Executable missing or not configured", File.Exists(_bridge.AdbPath)));
        summary.Children.Add(StatusRow("USB/IP tool", File.Exists(_bridge.UsbIpPath) ? "Executable found; signature and driver health require verification" : "Executable missing or not configured", false));
        summary.Children.Add(StatusRow("Phone", state.DeviceName ?? "No authenticated phone session", state.Paired));
        summary.Children.Add(StatusRow("Virtual driver", state.DriverStatus, false));
        summary.Children.Add(StatusRow("State", state.State + " · " + state.Detail, state.Paired));
        var actions = new WrapPanel { Margin = new Thickness(0, 22, 0, 0) };
        actions.Children.Add(Action("Export redacted report…", ExportDiagnostics));
        actions.Children.Add(Action("Refresh health checks", _bridge.RefreshAsync, true));
        actions.Children.Add(Action("Stop connection", _bridge.StopAsync, true));
        summary.Children.Add(actions);
        page.Children.Add(Card(summary));
        var recovery = new StackPanel();
        recovery.Children.Add(SectionTitle("When something needs attention"));
        recovery.Children.Add(Muted("No phone: try a data cable, unlock Android, and check USB debugging.\nAuthorization required: accept the intended computer on Android.\nPairing fails: compare both codes with the Android app open; revoke and reconnect if identities changed.\nApp sleeping: start its visible connection service and review Android battery restrictions.\nDriver unavailable: obtain a compatible signed USB/IP client; keep Windows protections enabled.\nApproval fails: inspect the phone for biometrics, invalidated keys, cancellation, or a locked screen."));
        page.Children.Add(Card(recovery));
        return page;
    }

    private StackPanel BuildAbout()
    {
        var page = Page("About", "");
        var panel = new StackPanel();
        panel.Children.Add(SectionTitle("KeyBridge 0.1.2"));
        panel.Children.Add(Muted("Android-backed security key for Windows. Open source under the MIT license."));
        panel.Children.Add(new TextBlock { Text = "Development release · not FIDO certified", Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 10, 0, 20) });
        var actions = new WrapPanel();
        actions.Children.Add(Action("GitHub", () => OpenUrl("https://github.com/devz-on/KeyBridge"), true));
        actions.Children.Add(Action("Sponsor", () => OpenUrl("https://github.com/sponsors/devz-on"), true));
        panel.Children.Add(actions);
        page.Children.Add(Card(panel));
        return page;
    }
    private Border Chip(string text, bool success)
    {
        return new Border { Background = Brush(success ? "SuccessSoftBrush" : "SurfaceBrush"), CornerRadius = new CornerRadius(18), Padding = new Thickness(11, 7, 11, 7), HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brush(success ? "SuccessBrush" : "AccentBrush") } };
    }

    private Border Card(UIElement child) => new() { Child = child, Style = (Style)FindResource("Card") };
    private TextBlock SectionTitle(string text) => new() { Text = text, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
    private TextBlock Muted(string text) => new() { Text = text, Style = (Style)FindResource("Muted") };
    private Brush Brush(string resource) => (Brush)FindResource(resource);

    private Grid StatusRow(string name, string detail, bool positive)
    {
        var row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var status = new TextBlock { Text = positive ? "✓" : "○", FontWeight = FontWeights.Bold, Foreground = Brush(positive ? "SuccessBrush" : "MutedBrush"), Margin = new Thickness(0, 1, 0, 0) };
        row.Children.Add(status);
        var label = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 0) };
        Grid.SetColumn(label, 1); row.Children.Add(label);
        var value = Muted(detail); Grid.SetColumn(value, 2); row.Children.Add(value);
        return row;
    }

    private Button Action(string title, Func<Task> action, bool secondary = false, bool enabled = true)
    {
        var button = new Button { Content = title, IsEnabled = enabled && !_busy };
        if (secondary) button.Style = (Style)FindResource("Secondary");
        button.Click += async (_, _) => await RunAction(action);
        return button;
    }

    private async Task RunAction(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        RefreshButton.IsEnabled = false;
        BusyIndicator.Visibility = Visibility.Visible;
        ErrorBanner.Visibility = Visibility.Collapsed;
        try { await action(); }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            ErrorBanner.Visibility = Visibility.Visible;
        }
        finally
        {
            _busy = false;
            RefreshButton.IsEnabled = true;
            BusyIndicator.Visibility = Visibility.Collapsed;
            if (!_exiting) RenderPage();
        }
    }

    private async Task InstallAndroid()
    {
        var file = new OpenFileDialog { Title = "Choose the KeyBridge Android APK", Filter = "Android package (*.apk)|*.apk", CheckFileExists = true, InitialDirectory = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "android")) };
        if (file.ShowDialog(this) == true)
        {
            if (MessageBox.Show(this, "Install this APK on the selected Android device? Use an APK built from source you trust.", "Install Android KeyBridge", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
            if (_selectedSerial is null) throw new InvalidOperationException("Select an authorized Android device first.");
            await _bridge.InstallAndroidAsync(_selectedSerial, file.FileName);
        }
    }

    private async Task ForgetPairing()
    {
        string serial = _selectedSerial ?? _bridge.Snapshot.SelectedSerial ?? throw new InvalidOperationException("Select the phone whose pairing should be forgotten.");
        if (MessageBox.Show(this, $"Forget the pinned identity for selected phone {serial} on this computer?\n\nThe current bridge connection will stop. Also revoke this computer in the Android app to remove its authorization there.", "Forget pairing", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK)
            await _bridge.ForgetPairingAsync(serial);
    }

    private async Task StopConnectionAsync()
    {
        try { await _bridge.StopAsync(); }
        catch (Exception ex) { ErrorText.Text = ex.Message; ErrorBanner.Visibility = Visibility.Visible; }
    }

    private async Task ExportDiagnostics()
    {
        var file = new SaveFileDialog { Title = "Export redacted KeyBridge diagnostics", FileName = "KeyBridge-diagnostics.json", Filter = "JSON report (*.json)|*.json" };
        if (file.ShowDialog(this) == true) await _bridge.ExportDiagnosticsAsync(file.FileName);
    }

    private static Task OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RunAction(_bridge.RefreshChecksAsync);
    private void DismissError(object sender, RoutedEventArgs e) => ErrorBanner.Visibility = Visibility.Collapsed;
    private static string GroupCode(string code) => string.Join("\n", code.Chunk(16).Select(row => string.Join(" ", row.Chunk(4).Select(group => new string(group)))));

    private static string SnapshotStamp(BridgeSnapshot state) => string.Join("\n", state.State, state.Detail, state.SelectedSerial, state.DeviceName, state.PairingCode, state.Paired, state.CredentialCount, state.DriverStatus, state.DriverInstalled, state.DriverSigned, state.ClientVerified, state.PhoneBiometricReady, state.PhoneSecurity, state.VirtualServerRunning, string.Join(";", state.Devices), string.Join(";", state.Activity));

    private static UiPreferences LoadPreferences()
    {
        try { return JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(PreferencePath)) ?? new(); }
        catch (IOException) { return new(); }
        catch (JsonException) { return new(); }
    }

    private void SavePreferences()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PreferencePath)!);
        File.WriteAllText(PreferencePath, JsonSerializer.Serialize(_preferences));
    }

    private void ApplyTheme()
    {
        var palette = _preferences.Dark
            ? new Dictionary<string, string> { ["CanvasBrush"] = "#191822", ["SurfaceBrush"] = "#24222F", ["SurfaceAltBrush"] = "#302D3D", ["TextBrush"] = "#F1EDF8", ["MutedBrush"] = "#B9B3CA", ["LineBrush"] = "#3A354B", ["AccentBrush"] = "#AB98F0", ["AccentTextBrush"] = "#251B3D", ["AccentSoftBrush"] = "#332B4B", ["SuccessBrush"] = "#95D5AF", ["SuccessSoftBrush"] = "#253E32", ["WarningBrush"] = "#F2C98E", ["WarningSoftBrush"] = "#483825" }
            : new Dictionary<string, string> { ["CanvasBrush"] = "#F6F5FA", ["SurfaceBrush"] = "#FFFFFF", ["SurfaceAltBrush"] = "#F1EFF7", ["TextBrush"] = "#222237", ["MutedBrush"] = "#6F6C82", ["LineBrush"] = "#E7E4EF", ["AccentBrush"] = "#6656BD", ["AccentTextBrush"] = "#FFFFFF", ["AccentSoftBrush"] = "#EEEAFF", ["SuccessBrush"] = "#276850", ["SuccessSoftBrush"] = "#E4F4EB", ["WarningBrush"] = "#94632B", ["WarningSoftBrush"] = "#FFF0D9" };
        foreach (var color in palette) System.Windows.Application.Current.Resources[color.Key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color.Value));
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
        return key?.GetValue("KeyBridge") is string;
    }

    private void SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (enabled)
            {
                var exe = System.IO.Path.Combine(AppContext.BaseDirectory, "KeyBridge.exe");
                if (!File.Exists(exe)) throw new InvalidOperationException("Cannot locate the KeyBridge executable. Use the published application for sign-in startup.");
                key.SetValue("KeyBridge", "\"" + exe + "\"");
            }
            else key.DeleteValue("KeyBridge", false);
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; ErrorBanner.Visibility = Visibility.Visible; }
    }

    private sealed class UiPreferences
    {
        public bool Dark { get; set; }
        public bool ReducedMotion { get; set; }
        public bool KeepInTray { get; set; }
        public bool Notifications { get; set; } = true;
    }

    private sealed record DeviceChoice(string Serial, string Display);
}
