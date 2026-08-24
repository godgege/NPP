using Microsoft.UI.Dispatching;
using Netch.App.Services;
using Velopack;

namespace Netch.App;

public static class Program
{
    private const string SingleInstanceMutexName = @"Local\NPP.SingleInstance";
    private const string ActivationEventName = @"Local\NPP.Activate";

    private static Mutex? _singleInstanceMutex;

    public static bool IsStartupLaunch { get; private set; }

    public static EventWaitHandle ActivationEvent { get; private set; } = null!;

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .Run();

        IsStartupLaunch = args.Any(arg => arg.Equals(StartupRegistrationService.StartupArgument, StringComparison.OrdinalIgnoreCase));

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }

            _singleInstanceMutex.Dispose();
            return;
        }

        ActivationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        WinRT.ComWrappersSupport.InitializeComWrappers();

        try
        {
            Microsoft.UI.Xaml.Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }
        finally
        {
            ActivationEvent.Dispose();
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
        }
    }
}
