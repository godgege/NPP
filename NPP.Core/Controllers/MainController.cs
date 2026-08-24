using System.Diagnostics;
using Microsoft.VisualStudio.Threading;
using Netch.Interfaces;
using Netch.Models;
using Netch.Models.Modes;
using Netch.Servers;
using Netch.Services;
using Netch.Utils;

namespace Netch.Controllers;

public class MainController
{
    private readonly NetchAppContext _appContext;
    private readonly IStatusReporter _statusReporter;
    private readonly ModeService _modeService;

    public MainController(NetchAppContext appContext, IStatusReporter statusReporter, ModeService modeService)
    {
        _appContext = appContext;
        _statusReporter = statusReporter;
        _modeService = modeService;
    }

    public Socks5Server? Socks5Server { get; private set; }

    public Server? Server { get; private set; }

    public Mode? Mode { get; private set; }

    public IServerController? ServerController { get; private set; }

    public IModeController? ModeController { get; private set; }

    public bool IsStopped => ServerController == null && ModeController == null;

    private readonly AsyncSemaphore Lock = new(1);

    public async Task StartAsync(Server server, Mode mode)
    {
        var releaser = await Lock.EnterAsync();
        var lockHeld = true;
        try
        {
            Log.Information("Start MainController: {Server} {Mode}", $"{server.Type}", $"[{(int)mode.Type}]{mode.i18NRemark}");

            if (await DnsUtils.LookupAsync(server.Hostname) == null)
                throw new MessageException(i18N.Translate("Lookup Server hostname failed"));

            // TODO Disable NAT Type Test setting
            // cache STUN Server ip to prevent "Wrong STUN Server"
            DnsUtils.LookupAsync(_appContext.Settings.STUN_Server).Forget();

            Server = server;
            Mode = mode;

            await Task.WhenAll(Task.Run(NativeMethods.RefreshDNSCache), Task.Run(() => Firewall.AddNetchFwRules(_appContext.NetchDir)));

            try
            {
                ModeController = _modeService.GetModeControllerByType(mode.Type, out var modePort, out var portName);

                if (modePort != null)
                    TryReleaseTcpPort((ushort)modePort, portName);

                if (Server is Socks5Server socks5 && (!socks5.Auth() || ModeController.Features.HasFlag(ModeFeature.SupportSocks5Auth)))
                {
                    Socks5Server = socks5;
                }
                else
                {
                    // Start Server Controller to get a local socks5 server
                    Log.Debug("Server Information: {Data}", $"{server.Type} {server.MaskedData()}");

                    ServerController = new V2rayController(_appContext);
                    _statusReporter.ReportStatus(i18N.TranslateFormat("Starting {0}", ServerController.Name));

                    TryReleaseTcpPort(ServerController.GetSocks5LocalPort(_appContext.Settings.Socks5LocalPort), "Socks5");
                    Socks5Server = await ServerController.StartAsync(server);

                    StatusPortInfoText.Socks5Port = Socks5Server.Port;
                    StatusPortInfoText.UpdateShareLan(_appContext.Settings.LocalAddress);
                }

                // Start Mode Controller
                _statusReporter.ReportStatus(i18N.TranslateFormat("Starting {0}", ModeController.Name));

                await ModeController.StartAsync(Socks5Server, mode);
            }
            catch (Exception e)
            {
                releaser.Dispose();
                lockHeld = false;
                try
                {
                    await StopAsync();
                }
                catch (Exception stopException)
                {
                    Log.Error(stopException, "MainController cleanup after start failure also failed");
                    throw new AggregateException("MainController failed to start and could not be stopped cleanly.", e, stopException);
                }

                switch (e)
                {
                    case DllNotFoundException:
                    case FileNotFoundException:
                        throw new Exception(e.Message + "\n\n" + i18N.Translate("Missing File or runtime components"));
                    case MessageException:
                        throw;
                    default:
                        Log.Error(e, "Unhandled Exception When Start MainController");
                        Utils.Utils.Open(Path.Combine(_appContext.DataRoot, Constants.LogFile));
                        throw new MessageException($"{i18N.Translate("Unhandled Exception")}\n{e.Message}");
                }
            }
        }
        finally
        {
            if (lockHeld)
                releaser.Dispose();
        }
    }

    public Task StopAsync()
    {
        return StopCoreAsync();
    }

    public Task StopForUpdateAsync()
    {
        return StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        using var _ = await Lock.EnterAsync();

        Log.Information("Stop Main Controller");
        var errors = new List<Exception>();

        if (ModeController is { } modeController)
        {
            try
            {
                await modeController.StopAsync();
                if (ReferenceEquals(ModeController, modeController))
                    ModeController = null;
            }
            catch (Exception e)
            {
                errors.Add(e);
                Log.Error(e, "Failed to stop mode controller {Controller}", modeController.Name);
            }
        }

        if (ServerController is { } serverController)
        {
            try
            {
                await serverController.StopAsync();
                if (ReferenceEquals(ServerController, serverController))
                    ServerController = null;
            }
            catch (Exception e)
            {
                errors.Add(e);
                Log.Error(e, "Failed to stop server controller {Controller}", serverController.Name);
            }
        }

        if (errors.Count > 0)
            throw new AggregateException("One or more MainController components failed to stop.", errors);

        Socks5Server = null;
        Server = null;
        Mode = null;
        StatusPortInfoText.Reset();
    }

    public void PortCheck(ushort port, string portName, PortType portType = PortType.Both)
    {
        try
        {
            PortHelper.CheckPort(port, portType);
        }
        catch (PortInUseException)
        {
            throw new MessageException(i18N.TranslateFormat("The {0} port is in use.", $"{portName} ({port})"));
        }
        catch (PortReservedException)
        {
            throw new MessageException(i18N.TranslateFormat("The {0} port is reserved by system.", $"{portName} ({port})"));
        }
    }

    public void TryReleaseTcpPort(ushort port, string portName)
    {
        foreach (var p in PortHelper.GetProcessByUsedTcpPort(port))
        {
            var fileName = p.MainModule?.FileName;
            if (fileName == null)
                continue;

            if (fileName.StartsWith(_appContext.NetchDir))
            {
                p.Kill();
                p.WaitForExit();
            }
            else
            {
                throw new MessageException(i18N.TranslateFormat("The {0} port is used by {1}.", $"{portName} ({port})", $"({p.Id}){fileName}"));
            }
        }

        PortCheck(port, portName, PortType.TCP);
    }

    public Task<NatTypeTestResult> DiscoveryNatTypeAsync(CancellationToken ctx = default)
    {
        Debug.Assert(Socks5Server != null, nameof(Socks5Server) + " != null");
        return Socks5ServerTestUtils.DiscoveryNatTypeAsync(Socks5Server, _appContext.Settings.STUN_Server, (ushort)_appContext.Settings.STUN_Server_Port, ctx);
    }

    public Task<int?> HttpConnectAsync(CancellationToken ctx = default)
    {
        Debug.Assert(Socks5Server != null, nameof(Socks5Server) + " != null");
        try
        {
            return Socks5ServerTestUtils.HttpConnectAsync(Socks5Server, ctx);
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception e)
        {
            Log.Warning(e, "Unhandled Socks5ServerTestUtils.HttpConnectAsync Exception");
        }

        return Task.FromResult<int?>(null);
    }
}
