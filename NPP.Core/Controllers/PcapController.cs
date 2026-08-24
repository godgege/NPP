using System.Text;
using Microsoft.VisualStudio.Threading;
using Netch.Interfaces;
using Netch.Models;
using Netch.Models.Modes;
using Netch.Models.Modes.ShareMode;
using Netch.Servers;
using Netch.Utils;

namespace Netch.Controllers;

public class PcapController : Guard, IModeController
{
    private readonly IStatusReporter _statusReporter;
    private ShareMode _mode = null!;
    private Socks5Server _server = null!;

    public PcapController(NetchAppContext appContext, IStatusReporter statusReporter)
        : base(appContext, "pcap2socks.exe", encoding: Encoding.UTF8)
    {
        _statusReporter = statusReporter;
    }

    protected override IEnumerable<string> StartedKeywords { get; } = new[] { "└" };

    public override string Name => "pcap2socks";

    public ModeFeature Features => ModeFeature.SupportSocks5Auth;

    public async Task StartAsync(Socks5Server server, Mode mode)
    {
        if (mode is not ShareMode shareMode)
            throw new InvalidOperationException();

        _server = server;
        _mode = shareMode;

        var outboundNetworkInterface = NetworkInterfaceUtils.GetBest();

        var arguments = new List<object?>
        {
            "--interface", $@"\Device\NPF_{outboundNetworkInterface.Id}",
            "--destination", $"{await _server.AutoResolveHostnameAsync()}:{_server.Port}",
            _mode.Argument, SpecialArgument.Flag
        };

        if (_server.Auth())
            arguments.AddRange(new[]
            {
                "--username", server.Username,
                "--password", server.Password
            });

        await StartGuardAsync(Arguments.Format(arguments));
    }

    public override async Task StopAsync()
    {
        await StopGuardAsync();
    }

    protected override void OnReadNewLine(string line)
    {
        Log.Debug("[pcap2socks] {Line}", line);
    }

    protected override void OnStarted()
    {
        _statusReporter.ReportStatus("pcap2socks started");
    }

    protected override void OnStartFailed()
    {
        if (new FileInfo(LogPath).Length == 0)
        {
            Task.Run(() =>
                {
                    Thread.Sleep(1000);
                    Utils.Utils.Open("https://github.com/zhxie/pcap2socks#dependencies");
                })
                .Forget();

            throw new MessageException("Pleases install pcap2socks's dependency");
        }

        Utils.Utils.Open(LogPath);
    }
}
