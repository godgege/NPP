namespace Netch.App.Services;

public interface INavigationService
{
    void NavigateTo(Type pageType);
    void GoBack();
}

public class NavigationService : INavigationService
{
    private Microsoft.UI.Xaml.Controls.Frame? _frame;

    public void SetFrame(Microsoft.UI.Xaml.Controls.Frame frame) => _frame = frame;

    public void NavigateTo(Type pageType)
    {
        _frame?.Navigate(pageType);
    }

    public void GoBack()
    {
        if (_frame?.CanGoBack == true)
            _frame.GoBack();
    }
}
