using System.Configuration;
using System.Data;
using System.Windows;

namespace WindowSnap;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = @"Local\WindowSnap.SingleInstance";
    private const string ActivateEventName = @"Local\WindowSnap.Activate";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activationWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        _instanceMutex = new Mutex(false, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            _activateEvent.Set();
            Shutdown();
            return;
        }

        MainWindow = new MainWindow();
        _activationWait = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => Dispatcher.BeginInvoke(new Action(() => (MainWindow as WindowSnap.MainWindow)?.ShowSettingsWindow())),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationWait?.Unregister(null);
        _activateEvent?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}

