using AegisVault.App.Services;
using AegisVault.Core.Models;
using AegisVault.Core.Services;
using Xunit;

namespace AegisVault.App.Tests;

public sealed class AutoLockServiceTests
{
    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    [Fact]
    public Task MinimizeTriggersLockWhenConfigured() => Headless.RunAsync<object?>(async () =>
    {
        var config = new UserConfig { LockOnMinimize = true };
        using var service = new AutoLockService(() => config);

        AutoLockTrigger? trigger = null;
        service.LockTriggered += value => trigger = value;

        service.SetMinimized(true);

        Assert.Equal(AutoLockTrigger.Minimize, trigger);
        return null;
    });

    [Fact]
    public Task MinimizeDoesNotTriggerWhenDisabled() => Headless.RunAsync<object?>(async () =>
    {
        var config = new UserConfig { LockOnMinimize = false, AutoLockMinutes = 0 };
        using var service = new AutoLockService(() => config);

        var raised = false;
        service.LockTriggered += _ => raised = true;

        service.SetMinimized(true);

        Assert.False(raised);
        return null;
    });

    [Fact]
    public Task IdleTriggersAfterConfiguredTime() => Headless.RunAsync<object?>(async () =>
    {
        var time = new FakeTimeProvider();
        var config = new UserConfig { LockOnMinimize = false, AutoLockMinutes = 1 };
        using var service = new AutoLockService(() => config, time);

        AutoLockTrigger? trigger = null;
        service.LockTriggered += value => trigger = value;

        time.Advance(TimeSpan.FromSeconds(61));
        service.SetMinimized(true);

        Assert.Equal(AutoLockTrigger.Idle, trigger);
        return null;
    });

    [Fact]
    public Task ReportActivityResetsIdleClock() => Headless.RunAsync<object?>(async () =>
    {
        var time = new FakeTimeProvider();
        var config = new UserConfig { LockOnMinimize = false, AutoLockMinutes = 1 };
        using var service = new AutoLockService(() => config, time);

        var raised = false;
        service.LockTriggered += _ => raised = true;

        time.Advance(TimeSpan.FromMinutes(10));
        service.ReportActivity();
        service.SetMinimized(false);
        service.SetMinimized(true);

        Assert.False(raised);
        return null;
    });

    [Fact]
    public Task ScreenLockTriggersWhenEnabled() => Headless.RunAsync<object?>(async () =>
    {
        var config = new UserConfig { LockOnScreenLock = true };
        using var service = new AutoLockService(() => config);

        AutoLockTrigger? trigger = null;
        service.LockTriggered += value => trigger = value;

        service.ReportScreenLocked();

        Assert.Equal(AutoLockTrigger.ScreenLock, trigger);
        return null;
    });

    [Fact]
    public Task SuspendTriggersWhenEnabled() => Headless.RunAsync<object?>(async () =>
    {
        var config = new UserConfig { LockOnSuspend = true };
        using var service = new AutoLockService(() => config);

        AutoLockTrigger? trigger = null;
        service.LockTriggered += value => trigger = value;

        service.ReportSuspended();

        Assert.Equal(AutoLockTrigger.Suspend, trigger);
        return null;
    });

    [Fact]
    public Task ScreenLockIgnoredWhenDisabled() => Headless.RunAsync<object?>(async () =>
    {
        var config = new UserConfig { LockOnScreenLock = false, AutoLockMinutes = 0 };
        using var service = new AutoLockService(() => config);

        var raised = false;
        service.LockTriggered += _ => raised = true;

        service.ReportScreenLocked();

        Assert.False(raised);
        return null;
    });

    /// <summary>
    /// The real trigger arrives on the <c>SystemEvents</c> broadcast thread, where
    /// everything downstream of <see cref="AutoLockService.LockTriggered"/> (window
    /// close, DispatcherTimer.Stop, vault dispose, lock overlay) would run off the UI
    /// thread. The service must marshal to the UI thread itself so callers cannot get
    /// this wrong; this test reports from a pool thread and asserts the handler ran
    /// on the UI thread.
    /// </summary>
    [Fact]
    public Task ScreenLockFromABackgroundThreadIsRaisedOnTheUiThread()
        => Headless.RunAsync<object?>(async () =>
    {
        var config = new UserConfig { LockOnScreenLock = true };
        using var service = new AutoLockService(() => config);

        var uiThread = Avalonia.Threading.Dispatcher.UIThread;
        AutoLockTrigger? trigger = null;
        var raisedOnUiThread = false;

        service.LockTriggered += value =>
        {
            trigger = value;
            raisedOnUiThread = uiThread.CheckAccess();
        };

        var background = Task.Run(() => service.ReportScreenLocked());

        // Posted work only runs when the headless loop gets a chance to process it.
        for (var i = 0; i < 200 && trigger is null; i++)
        {
            await Task.Delay(10);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        await background;
        Assert.Equal(AutoLockTrigger.ScreenLock, trigger);
        Assert.True(raisedOnUiThread, "LockTriggered must be raised on the UI thread");
        return null;
    });
}
