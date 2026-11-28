using Netch.Models;
using Timer = System.Timers.Timer;

namespace Netch.Utils;

public class DelayTestHelper
{
    private readonly NetchAppContext _appContext;
    private readonly Timer _timer;

    private readonly SemaphoreSlim _testLock = new(1, 1);

    private readonly SemaphoreSlim _poolLock = new(16, 16);

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

        _timer.Elapsed += (_, _) => _ = PerformTestAsync();
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
        if (!await _testLock.WaitAsync(0))
        {
            if (waitFinish)
            {
                await _testLock.WaitAsync();
                _testLock.Release();
            }

            return;
        }

        try
        {
            var tasks = _appContext.Settings.Server.Select(async s =>
            {
                await _poolLock.WaitAsync();
                try
                {
                    await s.PingAsync(_appContext.Settings.ServerTCPing);
                }
                finally
                {
                    _poolLock.Release();
                }
            });

            await Task.WhenAll(tasks);
        }
        catch (Exception)
        {
            // ignored
        }
        finally
        {
            _testLock.Release();
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
                _ = PerformTestAsync();
        }
    }
}
