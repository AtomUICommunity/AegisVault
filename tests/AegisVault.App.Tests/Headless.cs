using Avalonia.Headless;

namespace AegisVault.App.Tests;

/// <summary>
/// Shared headless Avalonia session. UI-touching test bodies must run through
/// <see cref="Run(Action)"/> / <see cref="RunAsync{T}(Func{Task{T}})"/>.
/// <para>
/// The session type is <see cref="Program"/> (not <c>App</c>) so Avalonia's
/// reflection lookup finds <c>Program.BuildAvaloniaApp</c>. Since AtomUI 6.2.6
/// that builder is the only place where AtomUI is configured, so pointing the
/// session at <c>App</c> would silently leave every control unstyled.
/// </para>
/// </summary>
internal static class Headless
{
    private static readonly HeadlessUnitTestSession Session =
        HeadlessUnitTestSession.StartNew(typeof(global::AegisVault.App.Program));

    public static Task Run(Action action)
        => Session.Dispatch(action, CancellationToken.None);

    public static Task<T> RunAsync<T>(Func<Task<T>> func)
        => Session.Dispatch(func, CancellationToken.None);
}
