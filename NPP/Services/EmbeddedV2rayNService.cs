using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Netch.App.Helpers;
using Serilog;

namespace Netch.App.Services;

public sealed class EmbeddedV2rayNService
{
    private const string StoppedStatus = "v2rayN 尚未启动";
    private readonly NetchAppContext _appContext;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private CancellationTokenSource _shutdown = new();
    private readonly EmbeddedWindow _window = new();
    private CompanionProcessJob? _job;
    private Process? _process;
    private bool _visible;
    private bool _stopping;

    public event EventHandler? StateChanged;
    public bool IsReady => !_stopping && _window.IsAttached && _process is { HasExited: false };
    public bool IsStarting { get; private set; }
    public string StatusText { get; private set; } = StoppedStatus;
    public string RuntimeDirectory => Path.Combine(_appContext.DataRoot, "clients", "v2rayN");

    public EmbeddedV2rayNService(NetchAppContext appContext) => _appContext = appContext;

    public async Task EnsureStartedAsync(nint owner)
    {
        await _lifecycleLock.WaitAsync(_shutdown.Token);
        try
        {
            if (IsReady)
                return;

            IsStarting = true;
            SetStatus("正在启动 v2rayN…");
            _window.Create(owner);
            await Task.Run(PrepareRuntime, _shutdown.Token);
            _shutdown.Token.ThrowIfCancellationRequested();

            _job?.Dispose();
            _process?.Dispose();
            _job = new CompanionProcessJob();
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(RuntimeDirectory, "v2rayN.exe"),
                WorkingDirectory = RuntimeDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            // Use the portable client's own defaults in an NPP-owned directory,
            // even if another v2rayN installation set this global environment flag.
            startInfo.Environment["V2RAYN_LOCAL_APPLICATION_DATA_V2"] = "0";
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 v2rayN。");
            _process = process;
            _job.Add(process);

            var trayRemoval = V2rayNPatch.TryRemoveStartupTrayIconAsync(process.Id,
                () => ReferenceEquals(process, _process) && !process.HasExited, _shutdown.Token);
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(30))
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                if (process.HasExited)
                    throw new InvalidOperationException($"v2rayN 已退出（代码 {process.ExitCode}）。");
                var handle = EmbeddedWindow.FindMainWindow(process.Id);
                if (handle != 0)
                {
                    _window.Attach(handle);
                    if (trayRemoval.IsCompleted && !await trayRemoval)
                        V2rayNPatch.TryRemoveTrayIcon(process.Id);
                    _window.SetVisible(_visible);
                    process.Exited += OnProcessExited;
                    process.EnableRaisingEvents = true;
                    IsStarting = false;
                    SetStatus("v2rayN 已就绪");
                    Log.Information("Embedded v2rayN started: PID {ProcessId}, directory {Directory}", process.Id, RuntimeDirectory);
                    return;
                }
                await Task.Delay(100, _shutdown.Token);
            }
            throw new TimeoutException("v2rayN 窗口启动超时，可点击重试。");
        }
        catch (Exception exception)
        {
            if (_process is { } process)
            {
                process.Exited -= OnProcessExited;
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception cleanupError) { Log.Warning(cleanupError, "Failed to stop v2rayN after startup failure"); }
                process.Dispose();
                _process = null;
            }
            _job?.Dispose();
            _job = null;
            IsStarting = false;
            _window.ClearChild();
            _window.SetVisible(false);
            SetStatus($"v2rayN 启动失败：{exception.GetBaseException().Message}");
            Log.Error(exception, "Embedded v2rayN startup failed");
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void PrepareRuntime()
    {
        var source = Path.Combine(_appContext.NetchDir, "clients", "v2rayN");
        var metadataPath = Path.Combine(source, "package.json");
        if (!File.Exists(metadataPath) || !File.Exists(Path.Combine(source, "v2rayN.exe")))
            throw new FileNotFoundException("缺少内置客户端资源，请运行 prepare-v2rayn.ps1 后重新构建。", metadataPath);

        var metadata = File.ReadAllText(metadataPath);
        using var package = JsonDocument.Parse(metadata);
        var required = package.RootElement.GetProperty("requiredFiles").EnumerateArray()
            .Select(value => value.GetString()!).ToArray();
        var marker = Path.Combine(RuntimeDirectory, ".npp-package.json");
        if (File.Exists(marker) && File.ReadAllText(marker) == metadata &&
            required.All(file => File.Exists(Path.Combine(RuntimeDirectory, file))))
            return;

        Directory.CreateDirectory(RuntimeDirectory);
        // Copy only packaged distribution files. guiConfigs/guiLogs/guiBackups
        // belong to the user and are never removed or replaced by an NPP update.
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            _shutdown.Token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar)[0].StartsWith("gui", StringComparison.OrdinalIgnoreCase))
                continue;
            var destination = Path.Combine(RuntimeDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
        if (required.Any(file => !File.Exists(Path.Combine(RuntimeDirectory, file))))
            throw new FileNotFoundException("内置 v2rayN 资源不完整，请重新安装 NPP。");
        File.WriteAllText(marker, metadata);
    }

    private void OnProcessExited(object? sender, EventArgs args)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_stopping || !ReferenceEquals(sender, _process))
                return;
            _job?.Dispose();
            _job = null;
            _window.ClearChild();
            _window.SetVisible(false);
            SetStatus("v2rayN 已退出，点击重试可重新打开。");
        });
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetBounds(int x, int y, int width, int height) => _window.SetBounds(x, y, width, height);

    public void SetVisible(bool visible)
    {
        _visible = visible;
        _window.SetVisible(visible && IsReady);
    }

    public async Task StopAsync()
    {
        _stopping = true;
        SetStatus("正在关闭 v2rayN…");
        await _shutdown.CancelAsync();
        await _lifecycleLock.WaitAsync();
        try
        {
            if (_process is { } process)
            {
                process.Exited -= OnProcessExited;
                if (!process.HasExited)
                {
                    try { await Task.Run(_window.RequestShutdown); }
                    catch (Exception exception) { Log.Warning(exception, "Could not invoke the v2rayN Exit command"); }
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await process.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException)
                    {
                        Log.Warning("v2rayN did not exit in time; stopping the companion process tree");
                        if (!process.HasExited)
                            process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync();
                    }
                }
                process.Dispose();
                _process = null;
            }
        }
        finally
        {
            _job?.Dispose();
            _job = null;
            _window.Dispose();
            _lifecycleLock.Release();
        }
    }

    public async Task ResumeAfterFailedUpdateAsync(nint owner, bool restartClient)
    {
        if (!_stopping)
            return;
        _shutdown.Dispose();
        _shutdown = new CancellationTokenSource();
        _stopping = false;
        if (restartClient)
            await EnsureStartedAsync(owner);
        else
            SetStatus(StoppedStatus);
    }
}
