using Netch.Interfaces;

namespace Netch.App.Services;

public class NotificationServiceImpl : INotificationService
{
    public void ShowNotification(string text, bool isInfo = true)
    {
        Serilog.Log.Information("[Notification] {Text}", text);
        var window = App.MainWindow;
        window?.DispatcherQueue.TryEnqueue(() => window.ShowNotification(text, isInfo));
    }
}
