using AegisVault.Core.Models;
using AegisVault.Core.Services;
using Avalonia.Threading;

namespace AegisVault.App.Services;

/// <summary>
/// Periodically evaluates <see cref="AutoLockPolicy"/> against user activity
/// and window state, raising <see cref="LockTriggered"/> when locking is due.
/// </summary>
public sealed class AutoLockService : IDisposable
{
    private readonly Func<UserConfig> _configProvider;
    private readonly TimeProvider _timeProvider;
    private readonly DispatcherTimer _timer;

    private DateTimeOffset _lastActivity;
    private bool _minimized;
    private bool _disposed;

    public event Action<AutoLockTrigger>? LockTriggered;

    public AutoLockService(Func<UserConfig> configProvider, TimeProvider? timeProvider = null)
    {
        _configProvider = configProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastActivity = _timeProvider.GetUtcNow();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => Evaluate();
        _timer.Start();
    }

    /// <summary>Resets the idle clock; also clears the minimized state.</summary>
    public void ReportActivity()
    {
        _lastActivity = _timeProvider.GetUtcNow();
        _minimized = false;
    }

    public void SetMinimized(bool minimized)
    {
        _minimized = minimized;
        if (minimized)
        {
            Evaluate();
        }
    }

    /// <summary>
    /// Reports a Windows session lock. Called from the <c>SystemEvents</c> broadcast
    /// thread, so the evaluation is marshalled onto the UI thread: everything
    /// downstream of <see cref="LockTriggered"/> touches Avalonia controls, timers and
    /// the SQLite session, none of which are thread safe.
    /// </summary>
    public void ReportScreenLocked() => EvaluateOnUiThread(screenLocked: true);

    /// <summary>
    /// Reports a system suspend. Same marshalling rules as
    /// <see cref="ReportScreenLocked"/>: the watcher raises this from the broadcast
    /// thread while the UI thread is still running.
    /// </summary>
    public void ReportSuspended() => EvaluateOnUiThread(suspended: true);

    /// <summary>
    /// Runs <see cref="Evaluate"/> on the UI thread.
    /// <para>
    /// Posting (rather than invoking synchronously) keeps the broadcast thread free and
    /// still makes the whole lock sequence atomic with respect to the UI thread — the
    /// earlier bug was that <c>LockVault</c> ran partly on the broadcast thread, so the
    /// vault could be disposed before the lock overlay was shown. Posting removes that
    /// interleaving entirely.
    /// </para>
    /// </summary>
    private void EvaluateOnUiThread(bool screenLocked = false, bool suspended = false)
    {
        var dispatcher = Dispatcher.UIThread;
        if (dispatcher.CheckAccess())
        {
            Evaluate(screenLocked, suspended);
            return;
        }

        dispatcher.Post(() => Evaluate(screenLocked, suspended));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
    }

    private void Evaluate(bool screenLocked = false, bool suspended = false)
    {
        if (_disposed)
        {
            return;
        }

        UserConfig config;
        try
        {
            config = _configProvider();
        }
        catch (Exception)
        {
            return;
        }

        var trigger = AutoLockPolicy.Evaluate(
            config,
            _timeProvider.GetUtcNow() - _lastActivity,
            minimized: _minimized,
            screenLocked: screenLocked,
            suspended: suspended);

        if (trigger == AutoLockTrigger.None)
        {
            return;
        }

        _timer.Stop();
        LockTriggered?.Invoke(trigger);
    }
}
