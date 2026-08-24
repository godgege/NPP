using Microsoft.VisualStudio.Threading;
using Netch.Models;
using Timer = System.Timers.Timer;

namespace Netch.Utils;

public class DelayTestHelper
{
    private readonly NetchAppContext _appContext;
    private readonly Timer _timer;

    private readonly AsyncSemaphore _lock = new(1);

    private readonly AsyncSemaphore _poolLock = new(16);

    public static readonly NumberRange Range = new(0, int.MaxValue / 1000);

    private bool _enabled = true;

    public DelayTestHelper(NetchAppContext appContext)
    {
        _appContext = appContext;

        _timer = new Timer
        {
            Interval = 10000,
            AutoReset = true
        };

        _timer.Elapsed += (_, _) => PerformTestAsync().Forget();
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            UpdateTick();
        }
    }

    /// <param name="waitFinish">if does not get lock, block until last release</param>
    public async Task PerformTestAsync(bool waitFinish = false)
    {
        if (_lock.CurrentCount == 0)
        {
            if (waitFinish)
                (await _lock.EnterAsync()).Dispose();

            return;
        }

        using var _ = await _lock.EnterAsync();

        try
        {
            var tasks = _appContext.Settings.Server.Select(async s =>
            {
                using (await _poolLock.EnterAsync())
                {
                    await s.PingAsync(_appContext.Settings.ServerTCPing);
                }
            });

            await Task.WhenAll(tasks);
        }
        catch (Exception)
        {
            // ignored
        }
    }

    public void UpdateTick(bool performTestAtOnce = false)
    {
        UpdateTick(_appContext.Settings.DetectionTick, performTestAtOnce);
    }

    /// <param name="interval">interval(seconds), 0 disable, MaxValue <c>int.MaxValue/1000</c></param>
    /// <param name="performTestAtOnce"></param>
    private void UpdateTick(int interval, bool performTestAtOnce = false)
    {
        _timer.Stop();

        var enable = Enabled && interval > 0 && Range.InRange(interval);
        if (enable)
        {
            _timer.Interval = interval * 1000;
            _timer.Start();
            if (performTestAtOnce)
                PerformTestAsync().Forget();
        }
    }
}
