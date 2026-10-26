using System;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows;

namespace KeyBridge.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;
    private Mutex? _upgradeGuard;

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            string user = identity.User?.Value ?? Environment.UserName;
            _instanceMutex = new Mutex(false, @"Local\KeyBridge.Desktop." + user);
            try { _ownsInstanceMutex = _instanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsInstanceMutex = true; }
            if (!_ownsInstanceMutex)
            {
                MessageBox.Show("KeyBridge is already running for this Windows user. Open it from the system tray or switch to its existing window.", "KeyBridge is already open", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("KeyBridge could not reserve its desktop session. " + ex.Message, "KeyBridge", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        _upgradeGuard = new Mutex(false, @"Local\KeyBridge.Desktop.UpgradeGuard");
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show("KeyBridge could not finish this action. " + args.Exception.Message, "KeyBridge", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstanceMutex)
        {
            _instanceMutex!.ReleaseMutex();
            _ownsInstanceMutex = false;
        }
        _instanceMutex?.Dispose();
        _upgradeGuard?.Dispose();
        base.OnExit(e);
    }
}
