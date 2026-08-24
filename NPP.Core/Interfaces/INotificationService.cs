namespace Netch.Interfaces;

public interface INotificationService
{
    void ShowNotification(string text, bool isInfo = true);
}
