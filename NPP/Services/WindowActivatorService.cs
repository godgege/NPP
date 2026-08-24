using Netch.Interfaces;

namespace Netch.App.Services;

public class WindowActivatorService : IWindowActivator
{
    public void ActivateMainWindow()
    {
        if (App.MainWindow != null)
        {
            App.MainWindow.Activate();
        }
    }
}
