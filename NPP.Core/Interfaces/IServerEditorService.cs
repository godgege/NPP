using Netch.Models;

namespace Netch.Interfaces;

public interface IServerEditorService
{
    void EditServer(Server s);
    void CreateServer(string typeName);
}
