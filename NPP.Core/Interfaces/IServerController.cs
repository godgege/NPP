using Netch.Models;
using Netch.Servers;

namespace Netch.Interfaces;

public interface IServerController : IController
{
    public ushort? Socks5LocalPort { get; set; }

    public string? LocalAddress { get; set; }

    public Task<Socks5Server> StartAsync(Server s);
}

public static class ServerControllerExtension
{
    public static ushort GetSocks5LocalPort(this IServerController controller, ushort defaultPort)
    {
        return controller.Socks5LocalPort ?? defaultPort;
    }

    public static string GetLocalAddress(this IServerController controller, string defaultAddress)
    {
        return controller.LocalAddress ?? defaultAddress;
    }
}
