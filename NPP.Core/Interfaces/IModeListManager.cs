using Netch.Models.Modes;

namespace Netch.Interfaces;

public interface IModeListManager
{
    void ReloadModes();
    void RemoveModeFromList(Mode mode);
}
