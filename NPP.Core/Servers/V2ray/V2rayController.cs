using System.Net;
using System.Text.Json;
using Netch.Controllers;
using Netch.Interfaces;
using Netch.Models;

namespace Netch.Servers;

public class V2rayController : Guard, IServerController
{
    private readonly NetchAppContext _appContext;

    public V2rayController(NetchAppContext appContext) : base(appContext, "v2ray-sn.exe")
    {
        _appContext = appContext;
    }

    protected override IEnumerable<string> StartedKeywords => new[] { "started" };

    protected override IEnumerable<string> FailedKeywords => new[] { "config file not readable", "failed to" };

    public override string Name => "V2Ray (SagerNet)";

    public ushort? Socks5LocalPort { get; set; }

    public string? LocalAddress { get; set; }

    public virtual async Task<Socks5Server> StartAsync(Server s)
    {
        await using (var fileStream = new FileStream(Constants.TempConfig, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            await JsonSerializer.SerializeAsync(fileStream, await V2rayConfigUtils.GenerateClientConfigAsync(s, _appContext.Settings), NetchAppContext.NewCustomJsonSerializerOptions());
        }

        await StartGuardAsync("run -c ..\\data\\last.json");
        return new Socks5Server(IPAddress.Loopback.ToString(), this.GetSocks5LocalPort(_appContext.Settings.Socks5LocalPort), s.Hostname);
    }
}
