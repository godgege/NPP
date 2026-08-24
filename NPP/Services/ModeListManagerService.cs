using Netch.Interfaces;
using Netch.Models.Modes;

namespace Netch.App.Services;

public class ModeListManagerService : IModeListManager
{
    public event Action? ModesReloaded;
    public event Action<Mode>? ModeRemoved;

    public void ReloadModes()
    {
        ModesReloaded?.Invoke();
    }

    public void RemoveModeFromList(Mode mode)
    {
        ModeRemoved?.Invoke(mode);
    }
}
