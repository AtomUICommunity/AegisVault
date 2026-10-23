using System.Diagnostics;
using AegisVault.App.Localization;
using AegisVault.App.Services;
using AegisVault.App.Theme;
using AegisVault.App.ViewModels;
using AegisVault.App.Views;
using AegisVault.Core.Models;
using AegisVault.Core.Services;
using AegisVault.Platform;
using AtomUI;
using AtomUI.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AegisVault.App;

public partial class App : Application
{
    /// <summary>Shortest time the splash stays up, so it never just flickers.</summary>
    private const int MinimumSplashMilliseconds = 500;

    /// <summary>Command line switch written into the auto-start entry.</summary>
    private const string TrayArgument = "--tray";

    /// <summary>
    /// Short yield after showing the splash so the compositor picks the window
    /// up before the (blocking) AtomUI initialisation starts. The first frame
    /// itself is rendered by the compositor thread, so it also arrives while
    /// the UI thread is busy.
    /// </summary>
    private const int SplashPresentSettleMilliseconds = 200;

    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private readonly AppPreferencesStore _preferencesStore = new();
    private AppPreferences _preferences = new();
    private SplashWindow? _splash;
    private VaultService? _vault;
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private AutoLockService? _autoLock;
    private ClipboardService? _clipboard;
    private SessionLockWatcher? _sessionWatcher;
    private SecureConfigService? _configService;
    private TrayIcon? _trayIcon;
    private QuickAccessWindow? _quickAccess;
    private bool _quickAccessPrewarmed;
    private BackupCoordinator? _backupCoordinator;

    /// <summary>Lets the open settings dialog refresh its backup status line.</summary>
    private Action<string?, string?>? SettingsBackupRefresh { get; set; }
    private FloatingBallWindow? _floatingBall;
    private HotKeyService? _hotKey;
    private readonly IAppRestarter _restarter = ProcessAppRestarter.Instance;
    private readonly IStartupRegistration _startupRegistration = StartupRegistrationFactory.Create();
    private bool _unlockDialogOpen;
    private bool _exiting;
    private bool _restarting;
    private bool _suppressCloseConfirm;
    private bool _closePromptOpen;
    private bool _startMinimizedOnLaunch;
    private bool _startMinimizedConsumed;
    private SettingsWindow? _settingsWindow;
    private PixelPoint? _mainWindowPosition;
    private PixelSize? _mainWindowSize;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        _preferences = _preferencesStore.Load();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            Loc.ApplyPreference(_preferences.Language);
            ApplyThemeVariant(_preferences.Theme);
            _startMinimizedOnLaunch = StartMinimizedRequested();
            ReconcileAutoStartEntry();

            if (PlatformSettings is { } platformSettings)
            {
                platformSettings.ColorValuesChanged += (_, _) =>
                {
                    if (_preferences.Theme == "system")
                    {
                        ApplyThemeVariant("system");
                    }
                };
            }

            _ = StartAsync(desktop);
            Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
        }
        else
        {
            // Headless test host and design-time: no interactive startup. AtomUI
            // itself was already configured on the AppBuilder (Program.BuildAvaloniaApp),
            // so nothing to initialise here.
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Staged startup: the brand splash goes up first, then the shell and vault
    /// are initialised, then the session hands over to the destination window
    /// (main or unlock). The frame yields let the compositor present the splash
    /// before the next stage blocks the UI thread.
    /// <para>
    /// Since AtomUI 6.2.6 the theme is committed on the AppBuilder before this
    /// Application is constructed (see <c>Program.BuildAvaloniaApp</c>), so the
    /// splash no longer has to cover AtomUI initialisation. It still earns its
    /// place: it covers vault opening and the window handover.
    /// </para>
    /// </summary>
    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var splash = new SplashWindow(_preferences.Theme, _preferences.ReduceMotion);
        _splash = splash;
        var splashShownAt = Stopwatch.GetTimestamp();
        var startupClock = Stopwatch.StartNew();
        long shellMilliseconds = 0;
        long vaultMilliseconds = 0;

        try
        {
            await YieldFrameAsync();
            splash.Show();
            // The first frame is rendered by the compositor thread (so it arrives
            // even while the UI thread is busy); the yield below is just courtesy.
            await Task.Delay(SplashPresentSettleMilliseconds);
            await YieldFrameAsync();

            splash.SetStatus(Loc.T("Splash_LoadingShell"), 0.45);
            await YieldFrameAsync();

            InitializeAtomUI();
            InitializeTray();
            InitializeHotKey();
            await YieldFrameAsync();
            shellMilliseconds = startupClock.ElapsedMilliseconds;

            splash.SetStatus(Loc.T("Splash_OpeningVault"), 0.75);
            await ShowUnlockAsync(desktop);
            vaultMilliseconds = startupClock.ElapsedMilliseconds - shellMilliseconds;
            await CloseSplashAsync(splash, splashShownAt);

            StartupLog.Append(
                $"started in {startupClock.ElapsedMilliseconds}ms " +
                $"(shell {shellMilliseconds}ms, vault {vaultMilliseconds}ms, theme {_preferences.Theme})");
        }
        catch (Exception exception)
        {
            // Startup must never leave a frozen splash behind: close it and fall
            // back to the unlock window (or exit) instead of a zombie process.
            Trace.TraceError($"Startup failed: {exception}");
            _splash = null;

            try
            {
                splash.Close();
                ShowUnlockFallback(desktop);
            }
            catch (Exception fallbackException)
            {
                Trace.TraceError($"Startup fallback failed: {fallbackException}");
                _desktop?.Shutdown();
            }
        }
    }

    /// <summary>Last-resort window after a failed startup.</summary>
    private void ShowUnlockFallback(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var viewModel = new UnlockViewModel
        {
            DeviceKeyProtector = CreateKeyProtector(),
            SecureInputAvailable = OperatingSystem.IsWindows(),
        };
        viewModel.ApplyPreferences(_preferences);
        CreateUnlockWindow(desktop, viewModel);
    }

    /// <summary>
    /// Last-resort guard for exceptions raised from UI callbacks (commands,
    /// native tray/hotkey callbacks): report instead of dying silently mid-action.
    /// </summary>
    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Trace.TraceError($"Unhandled UI exception: {e.Exception}");
        e.Handled = true;

        if (_mainViewModel is { } viewModel)
        {
            viewModel.StatusMessage = Loc.T("Main_StatusUnexpectedError");
        }
    }

    /// <summary>
    /// AtomUI is configured on the <see cref="Avalonia.AppBuilder"/> now
    /// (<c>Program.BuildAvaloniaApp</c>), because 6.2.6 moved the entry point there
    /// and commits it before this Application is constructed. Startup only needs
    /// to let the staged flow continue.
    /// </summary>
    private static void InitializeAtomUI()
    {
        // Intentionally empty: kept so the staged startup reads the same as before
        // and future post-commit work has an obvious home.
    }

    /// <summary>
    /// Yields at a priority below rendering, so the pending layout/animation
    /// frame is composited before the next (blocking) startup stage runs.
    /// </summary>
    private static async Task YieldFrameAsync()
        => await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

    private async Task CloseSplashAsync(SplashWindow splash, long shownAt)
    {
        var remaining = TimeSpan.FromMilliseconds(MinimumSplashMilliseconds)
            - Stopwatch.GetElapsedTime(shownAt);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining);
        }

        if (ReferenceEquals(_splash, splash))
        {
            _splash = null;
        }

        await splash.FadeOutAndCloseAsync();
    }

    private void InitializeTray()
    {
        try
        {
            var trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://AegisVault.App/Assets/tray.png"))),
                ToolTipText = Loc.T("App_Title"),
            };
            trayIcon.Clicked += OnTrayClicked;

            var menu = new NativeMenu();
            menu.Add(CreateMenuItem(Loc.T("Tray_Open"), OnTrayOpenClicked));
            menu.Add(CreateMenuItem(Loc.T("Tray_QuickAccess"), OnTrayQuickAccessClicked));
            menu.Add(CreateMenuItem(Loc.T("Tray_Settings"), OnTraySettingsClicked));
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(CreateMenuItem(Loc.T("Tray_Lock"), OnTrayLockClicked));
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(CreateMenuItem(Loc.T("Tray_Exit"), OnTrayExitClicked));
            trayIcon.Menu = menu;

            TrayIcon.SetIcons(this, new TrayIcons { trayIcon });
            _trayIcon = trayIcon;
        }
        catch (Exception)
        {
            // Tray integration is best effort; the application stays usable without it.
        }
    }

    private static NativeMenuItem CreateMenuItem(string header, EventHandler handler)
    {
        var item = new NativeMenuItem(header);
        item.Click += handler;
        return item;
    }

    private async Task ShowUnlockAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var viewModel = new UnlockViewModel
        {
            DeviceKeyProtector = CreateKeyProtector(),
            SecureInputAvailable = OperatingSystem.IsWindows(),
        };
        viewModel.ApplyPreferences(_preferences);

        // Try the remembered device key before anything is shown: on success
        // the vault goes straight to the main window and the unlock window is
        // never created (creating it first made it flash before the main one).
        if (viewModel.CanRememberDevice)
        {
            var vault = await viewModel.TryDeviceUnlockAsync();
            if (vault is not null)
            {
                ShowMain(desktop, unlockWindow: null, vault);
                return;
            }
        }

        CreateUnlockWindow(desktop, viewModel);
    }

    private void CreateUnlockWindow(
        IClassicDesktopStyleApplicationLifetime desktop,
        UnlockViewModel viewModel)
    {
        var window = new UnlockWindow { DataContext = viewModel };
        viewModel.VaultOpened += vault => ShowMain(desktop, window, vault);

        desktop.MainWindow = window;
        window.Show();
    }

    private void ShowMain(
        IClassicDesktopStyleApplicationLifetime desktop,
        UnlockWindow? unlockWindow,
        VaultService vault)
    {
        var config = new SecureConfigService(vault);
        _configService = config;
        var clipboard = new ClipboardService(
            new AvaloniaClipboardAccess(() => (TopLevel?)_mainWindow ?? unlockWindow),
            () => config.Current);
        var autoLock = new AutoLockService(() => config.Current);
        var sessionWatcher = new SessionLockWatcher();
        sessionWatcher.ScreenLocked += autoLock.ReportScreenLocked;
        sessionWatcher.Suspended += autoLock.ReportSuspended;
        var viewModel = new MainViewModel(
            vault,
            clipboard,
            searchDebounce: TimeSpan.FromMilliseconds(120),
            healthDebounce: TimeSpan.FromMilliseconds(200),
            openDebounce: TimeSpan.FromSeconds(2));
        viewModel.ThemePreference = _preferences.Theme;
        viewModel.LanguagePreference = _preferences.Language;
        viewModel.AttachAppearanceCallbacks(ApplyTheme, ApplyLanguage);

        // "Remember this device" is best effort; tell the user when it failed
        // instead of leaving them with a silently missing device key.
        if (unlockWindow?.DataContext is ViewModels.UnlockViewModel { RememberDeviceFailed: true })
        {
            viewModel.StatusMessage = Loc.T("Unlock_ErrorRememberDeviceFailed");
        }
        viewModel.StartupGuideDismissed = _preferences.StartupGuideDismissed;
        viewModel.AttachStartupGuideCallback(() =>
        {
            _preferences = _preferences with { StartupGuideDismissed = true };
            SavePreferences();
        });
        var window = _mainWindow;

        if (window is null)
        {
            window = new MainWindow();
            _mainWindow = window;
            // Subscribe exactly once per window: the lock overlay must be able
            // to raise UnlockRequested while the vault is locked (the handler
            // is deliberately NOT removed in LockVault).
            window.UnlockRequested += OnUnlockRequested;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_mainWindow, window))
                {
                    // The main window is the session; closing it exits. Keeping
                    // the process alive would leave a tray icon that can no
                    // longer open anything.
                    ExitApplication(restart: false);
                }
            };
            desktop.MainWindow = window;
            // Window.Position only reflects programmatic moves: a title-bar drag is
            // performed by the platform (extended client area), so track the live
            // values through the change notifications instead.
            window.PositionChanged += (_, args) => _mainWindowPosition = args.Point;
            window.SizeChanged += (_, args) => _mainWindowSize = new PixelSize(
                (int)args.NewSize.Width,
                (int)args.NewSize.Height);
            window.Closing += (_, _) => SaveMainWindowPlacement(window);
            window.Closing += OnMainWindowClosing;
            window.PropertyChanged += (_, change) =>
            {
                if (change.Property == Avalonia.Controls.Window.WindowStateProperty)
                {
                    SaveMainWindowPlacement(window);
                }
            };
        }

        _vault = vault;
        _mainViewModel = viewModel;
        _autoLock = autoLock;
        _clipboard = clipboard;
        _sessionWatcher = sessionWatcher;

        _preferences = _preferences with
        {
            LastVaultPath = vault.VaultPath,
            RecentVaultPaths = RecentVaults.Add(_preferences.RecentVaultPaths, vault.VaultPath),
        };
        SavePreferences();

        window.Attach(
            viewModel,
            clipboard,
            autoLock,
            ShowSettings,
            ApplyScreenGuard,
            () => _configService?.Current.Generator);
        viewModel.LockRequested += LockVault;
        autoLock.LockTriggered += _ => LockVault();

        // Geometry before the first show so the window never appears at the default
        // spot and then jumps; the screens are validated afterwards.
        ApplyMainWindowPlacement(window);

        // Tray mode (auto-start / StartMinimized): the window exists and stays
        // open, but is not shown; the tray icon and hotkey bring it back.
        if (_startMinimizedOnLaunch && !_startMinimizedConsumed)
        {
            _startMinimizedConsumed = true;
            window.Show();
            window.Hide();
        }
        else
        {
            window.Show();
            window.WindowState = WindowState.Normal;
            window.Activate();
        }

        EnsureMainWindowOnScreen(window);
        if (_preferences.MainWindowMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }

        unlockWindow?.Close();

        ApplyScreenGuard(window);
        UpdateFloatingBall();

        // Local backups: rebuild the coordinator for the new session and let it
        // run immediately when the vault is already due (e.g. after unlock).
        StartBackupCoordinator();

        // Deferred: the first hotkey press should not pay for the quick-access
        // XAML load and native window creation, but the prewarm must not delay
        // the main window's first frame either.
        Dispatcher.UIThread.Post(PrewarmQuickAccess, DispatcherPriority.Background);
    }

    /// <summary>
    /// Restores the remembered main-window rectangle. Called before the window is
    /// shown, so it never appears at the default location first.
    /// </summary>
    private void ApplyMainWindowPlacement(MainWindow window)
    {
        if (!WindowPlacement.TryParse(_preferences.MainWindowBounds, out var position, out var size))
        {
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = position;
        window.Width = size.Width;
        window.Height = size.Height;
    }

    /// <summary>
    /// Falls back to a centred window when the remembered rectangle no longer
    /// overlaps any screen (monitor removed, resolution changed, undocked).
    /// </summary>
    private static void EnsureMainWindowOnScreen(MainWindow window)
    {
        if (window.WindowState == WindowState.Maximized)
        {
            return;
        }

        var screens = window.Screens;
        var areas = screens.All
            .Select(screen => screen.WorkingArea)
            .Select(area => new Rect(area.X, area.Y, area.Width, area.Height))
            .ToList();

        var size = new PixelSize((int)window.Width, (int)window.Height);
        if (areas.Count == 0 || WindowPlacement.IsVisibleOnScreens(window.Position, size, areas))
        {
            return;
        }

        var target = screens.Primary ?? screens.All.FirstOrDefault();
        if (target is null)
        {
            return;
        }

        var working = target.WorkingArea;
        window.Position = new PixelPoint(
            working.X + Math.Max(0, (working.Width - size.Width) / 2),
            working.Y + Math.Max(0, (working.Height - size.Height) / 2));
    }

    /// <summary>Persists the current rectangle (and maximized state) of the window.</summary>
    private void SaveMainWindowPlacement(MainWindow window)
    {
        // While maximized (or minimized) the live size is not the restored one, so
        // keep the rectangle that was last seen in the normal state.
        var bounds = window.WindowState == WindowState.Normal
            ? WindowPlacement.Format(
                _mainWindowPosition ?? window.Position,
                _mainWindowSize ?? new PixelSize((int)window.Width, (int)window.Height))
            : _preferences.MainWindowBounds;

        var maximized = window.WindowState == WindowState.Maximized;
        if (bounds == _preferences.MainWindowBounds && maximized == _preferences.MainWindowMaximized)
        {
            return;
        }

        _preferences = _preferences with
        {
            MainWindowBounds = bounds,
            MainWindowMaximized = maximized,
        };
        SavePreferences();
    }

    private void LockVault()
    {
        if (_mainWindow is null && _vault is null)
        {
            return;
        }

        var window = _mainWindow;
        if (window is null)
        {
            CleanupSession();
            if (_desktop is { } desktop)
            {
                _ = ShowUnlockAsync(desktop);
            }
            return;
        }

        CleanupSession(keepWindow: true);
        window.DataContext = new LockViewModel();
        window.ShowLockOverlay();
        window.Activate();
    }

    private async void OnUnlockRequested()
    {
        if (_mainWindow is null || _unlockDialogOpen)
        {
            return;
        }

        _unlockDialogOpen = true;
        try
        {
            var viewModel = new UnlockViewModel
            {
                DeviceKeyProtector = CreateKeyProtector(),
                SecureInputAvailable = OperatingSystem.IsWindows(),
            };
            viewModel.ApplyPreferences(_preferences);
            var window = new UnlockWindow { DataContext = viewModel };
            viewModel.VaultOpened += vault => ShowMain(_desktop!, window, vault);
            await window.ShowDialog(_mainWindow);
        }
        catch (Exception)
        {
        }
        finally
        {
            _unlockDialogOpen = false;
        }
    }

    private void CleanupSession(bool keepWindow = false)
    {
        // A dialog that outlives the session would hit a disposed vault on its
        // next command; close it as part of the teardown.
        CloseSettingsWindow();

        StopBackupCoordinator();

        _quickAccess?.Close();
        _quickAccess = null;
        _quickAccessPrewarmed = false;

        _sessionWatcher?.Dispose();
        _sessionWatcher = null;

        _configService = null;

        _autoLock?.Dispose();
        _autoLock = null;

        _clipboard?.Dispose();
        _clipboard = null;

        if (_mainViewModel is not null)
        {
            _mainViewModel.LockRequested -= LockVault;
            _mainViewModel.Dispose();
            _mainViewModel = null;
        }

        _vault?.Dispose();
        _vault = null;
        if (!keepWindow)
        {
            _mainWindow = null;
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    /// <summary>
    /// Decides what closing the main window does: exit immediately, hide to the
    /// tray, or ask. Runs before the window's own clipboard cleanup, so a
    /// cancelled close (minimize/ask) keeps the live session intact.
    /// </summary>
    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (sender is not Avalonia.Controls.Window window || _exiting || _suppressCloseConfirm)
        {
            return;
        }

        switch (CloseActions.Normalize(_preferences.CloseAction))
        {
            case CloseActions.Exit:
                return;

            case CloseActions.Minimize:
                e.Cancel = true;
                MinimizeMainWindowToTray(window);
                return;

            default:
                e.Cancel = true;
                _ = ObserveDialogAsync(PromptCloseAsync(window));
                return;
        }
    }

    private async Task PromptCloseAsync(Avalonia.Controls.Window window)
    {
        if (_closePromptOpen)
        {
            return;
        }

        _closePromptOpen = true;
        try
        {
            var dialog = new CloseConfirmWindow(
                Loc.T("Main_CloseConfirmTitle"),
                Loc.T("Main_CloseConfirmMessage"));

            var result = await dialog.ShowDialog<ClosePromptResult>(window);
            if (result == ClosePromptResult.Cancel)
            {
                return;
            }

            if (dialog.DontAskAgain)
            {
                _preferences = _preferences with
                {
                    CloseAction = result == ClosePromptResult.Exit ? CloseActions.Exit : CloseActions.Minimize,
                };
                SavePreferences();
            }

            if (result == ClosePromptResult.Exit)
            {
                // Let the second close request through untouched.
                _suppressCloseConfirm = true;
                window.Close();
            }
            else
            {
                MinimizeMainWindowToTray(window);
            }
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    private static void MinimizeMainWindowToTray(Avalonia.Controls.Window window)
    {
        // Route through WindowState: the main window's own handler hides it to
        // the tray and lets the existing "lock on minimize" policy apply.
        window.WindowState = WindowState.Minimized;
    }

    /// <summary>
    /// Creates the backup coordinator for the current session. Backups run while
    /// the vault is unlocked <em>or</em> locked (a snapshot never decrypts), but
    /// the session object is needed for the path, so it is rebuilt per session.
    /// </summary>
    private void StartBackupCoordinator()
    {
        StopBackupCoordinator();
        if (_vault is null)
        {
            return;
        }

        _backupCoordinator = new BackupCoordinator(
            new LocalBackupService(_vault.VaultPath),
            _vault,
            () => _preferences,
            preferences =>
            {
                _preferences = preferences;
                SavePreferences();
            },
            CurrentAppVersion());
        _backupCoordinator.Start();

        // An unlock can already be due (e.g. the interval elapsed while locked),
        // so probe once without waiting for the first timer tick.
        Dispatcher.UIThread.Post(
            () => _ = RunAutomaticBackupAsync(),
            DispatcherPriority.Background);
    }

    private void StopBackupCoordinator()
    {
        _backupCoordinator?.Dispose();
        _backupCoordinator = null;
    }

    private async Task RunAutomaticBackupAsync()
    {
        var coordinator = _backupCoordinator;
        if (coordinator is null)
        {
            return;
        }

        var result = await coordinator.RunOnceAsync(automatic: true);
        if (!result.Success && result.FailureCode is not null)
        {
            // Only surface real failures, not "nothing to do" skips.
            var skip = Enum.TryParse<BackupSkipReason>(result.FailureCode, out var parsed) ? parsed : BackupSkipReason.None;
            if (skip == BackupSkipReason.None)
            {
                _mainViewModel?.StatusMessage = Loc.T("Main_BackupFailed");
            }
        }

        SettingsBackupRefresh?.Invoke(_preferences.BackupLastSuccessUtc, _preferences.BackupLastResult);
    }

    /// <summary>Runs a user-triggered backup from the settings window.</summary>
    private async Task<BackupResult> CreateBackupNowAsync()
    {
        if (_backupCoordinator is null)
        {
            return BackupResult.Failed("no-session", "The vault is locked.");
        }

        var result = await _backupCoordinator.RunOnceAsync(automatic: false);
        SettingsBackupRefresh?.Invoke(_preferences.BackupLastSuccessUtc, _preferences.BackupLastResult);
        return result;
    }

    private static string CurrentAppVersion()
        => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private void OnTrayClicked(object? sender, EventArgs e) => ShowMainWindow();

    private void OnTrayOpenClicked(object? sender, EventArgs e) => ShowMainWindow();

    private void OnTrayQuickAccessClicked(object? sender, EventArgs e) => ToggleQuickAccess();

    private void OnTraySettingsClicked(object? sender, EventArgs e)
    {
        ShowMainWindow();
        ShowSettings();
    }

    private void OnTrayLockClicked(object? sender, EventArgs e) => LockVault();

    private void OnTrayExitClicked(object? sender, EventArgs e) => ExitApplication(restart: false);

    /// <summary>Closes the session and optionally spawns a fresh instance.</summary>
    private void ExitApplication(bool restart)
    {
        if (_exiting)
        {
            return;
        }

        if (restart)
        {
            // The replacement process would see the still-held instance lock and
            // exit as a "second instance", so hand the lock over first.
            var handedOver = Program.Instance?.Release() == true;

            if (!_restarter.TryStartNewInstance())
            {
                if (handedOver)
                {
                    Program.Instance?.TryReacquire();
                }

                // Keep the current session alive rather than leaving the user with nothing.
                _mainViewModel?.StatusMessage = Loc.T("Main_RestartManualHint");
                return;
            }
        }

        _exiting = true;
        CleanupSession();
        _hotKey?.Dispose();
        _floatingBall?.Close();
        _splash?.Close();
        _splash = null;
        _desktop?.Shutdown();
    }

    private void InitializeHotKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var hotKey = new HotKeyService();
            if (hotKey.TryRegister(HotKeyService.ModControl | HotKeyService.ModShift, 0x20 /* VK_SPACE */))
            {
                hotKey.HotKeyPressed += () => Dispatcher.UIThread.Post(ToggleQuickAccess);
                _hotKey = hotKey;
            }
            else
            {
                hotKey.Dispose();
            }
        }
        catch (Exception)
        {
            // Global hotkey is best effort; quick access stays available via tray.
        }
    }

    private void ToggleQuickAccess()
    {
        if (_mainViewModel is null || _mainWindow is null)
        {
            // Locked or not yet unlocked: bring the main window up instead.
            ShowMainWindow();
            return;
        }

        if (_quickAccess is null)
        {
            _quickAccess = CreateQuickAccess(new QuickAccessViewModel(_mainViewModel, _clipboard));
        }

        if (_quickAccess.IsVisible)
        {
            SetQuickAccessActive(false);
            _quickAccess.Hide();
            _mainWindow.Activate();
            return;
        }

        SetQuickAccessActive(true);
        var openStartedAt = Stopwatch.GetTimestamp();
        _quickAccess.Show();
        _quickAccess.Activate();
        ApplyScreenGuard(_quickAccess);
        StartupLog.Append($"quick access opened in {Stopwatch.GetElapsedTime(openStartedAt).TotalMilliseconds:0}ms (prewarmed: {_quickAccessPrewarmed})");
    }

    private QuickAccessWindow CreateQuickAccess(QuickAccessViewModel viewModel)
    {
        viewModel.EntryActivated += ShowMainWindow;
        var quickAccess = new QuickAccessWindow { DataContext = viewModel };
        quickAccess.Closed += (_, _) =>
        {
            viewModel.Dispose();

            // The user may close it with Alt+F4; a closed Avalonia window
            // cannot be shown again, so forget it and recreate on demand.
            if (ReferenceEquals(_quickAccess, quickAccess))
            {
                _quickAccess = null;
                _quickAccessPrewarmed = false;
            }
        };
        return quickAccess;
    }

    /// <summary>
    /// Builds the quick-access window ahead of the first hotkey so XAML parsing
    /// and native window creation are paid at unlock time instead. Deferred to
    /// the background so it can never delay the main window's first frame.
    /// </summary>
    private void PrewarmQuickAccess()
    {
        if (_quickAccess is not null || _mainViewModel is null || _mainWindow is null)
        {
            return;
        }

        var startedAt = Stopwatch.GetTimestamp();
        var quickAccess = CreateQuickAccess(new QuickAccessViewModel(_mainViewModel, _clipboard));
        (quickAccess.DataContext as QuickAccessViewModel)?.SetActive(false);
        _quickAccess = quickAccess;
        _quickAccessPrewarmed = true;
        StartupLog.Append($"quick access prewarmed in {Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:0}ms");
    }

    private void SetQuickAccessActive(bool active)
        => (_quickAccess?.DataContext as QuickAccessViewModel)?.SetActive(active);

    private void UpdateFloatingBall()
    {
        if (_mainWindow is null || !_preferences.ShowFloatingBall)
        {
            _floatingBall?.Close();
            _floatingBall = null;
            return;
        }

        if (_floatingBall is null)
        {
            var ball = new FloatingBallWindow(_preferences.ReduceMotion);
            ball.BallClicked += ToggleQuickAccess;
            ball.PlacementChanged += OnBallPlacementChanged;
            ball.Closed += (_, _) =>
            {
                if (ReferenceEquals(_floatingBall, ball))
                {
                    _floatingBall = null;
                }
            };
            _floatingBall = ball;
        }

        if (!_floatingBall.IsVisible)
        {
            _floatingBall.Show();

            if (BallPlacement.TryParse(_preferences.BallPosition, out var saved))
            {
                _floatingBall.ApplyPlacement(saved, _preferences.BallDockedSide);
            }
        }
    }

    private void OnBallPlacementChanged(PixelPoint position, string? dockedSide)
    {
        _preferences = _preferences with
        {
            BallPosition = BallPlacement.Format(position),
            BallDockedSide = dockedSide,
        };

        try
        {
            _preferencesStore.Save(_preferences);
        }
        catch (Exception exception)
        {
            // The ball position is a convenience preference; an IO failure must
            // not escape into the window's drag event.
            Trace.TraceWarning($"Saving the floating ball placement failed: {exception.Message}");
        }
    }

    private void ApplyScreenGuard(Avalonia.Controls.Window window)
    {
        // null config (locked) keeps the guard on; only an explicit opt-out lifts it.
        var exclude = _configService?.Current.DisableScreenCapture != false;

        try
        {
            var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
            ScreenCaptureGuard.TrySetExcluded(handle, exclude);
        }
        catch (Exception)
        {
            // Screen capture exclusion is best effort.
        }
    }

    private static IKeyProtector? CreateKeyProtector()
        => OperatingSystem.IsWindows() ? new DpapiKeyProtector() : null;

    private static string ExecutablePath => Environment.ProcessPath ?? string.Empty;

    /// <summary>True when this launch should go straight to the tray.</summary>
    private bool StartMinimizedRequested()
        => _preferences.StartMinimized ||
           Environment.GetCommandLineArgs().Skip(1)
               .Any(argument => string.Equals(argument, TrayArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Re-creates the auto-start entry when the preference says it is on but the
    /// registry no longer points at this executable (update, reinstall, move).
    /// </summary>
    private void ReconcileAutoStartEntry()
    {
        if (!_preferences.AutoStart || !_startupRegistration.IsSupported)
        {
            return;
        }

        try
        {
            if (!_startupRegistration.IsEnabled(ExecutablePath))
            {
                _startupRegistration.TrySetEnabled(true, ExecutablePath, TrayArgument);
            }
        }
        catch (Exception)
        {
            // Auto-start is best effort; the settings page reports failures.
        }
    }

    private void SavePreferences()
    {
        try
        {
            _preferencesStore.Save(_preferences);
        }
        catch (Exception)
        {
            // Preferences are non-critical; never block the session on them.
        }
    }

    private void ShowSettings()
    {
        if (_mainWindow is null || _vault is null || _configService is null)
        {
            return;
        }

        // Only one settings dialog at a time (tray + toolbar can both ask).
        if (_settingsWindow is { } existing)
        {
            existing.Activate();
            return;
        }

        var viewModel = new SettingsViewModel(
            _vault,
            _configService,
            CreateKeyProtector(),
            ApplyTheme,
            _preferences.Theme,
            screenGuardSupported: ScreenCaptureGuard.IsSupported,
            applyScreenGuard: _ =>
            {
                if (_mainWindow is { } window)
                {
                    ApplyScreenGuard(window);
                }

                if (_quickAccess is { IsVisible: true } quickAccess)
                {
                    ApplyScreenGuard(quickAccess);
                }

                if (_settingsWindow is { } settings)
                {
                    ApplyScreenGuard(settings);
                }
            },
            showFloatingBall: _preferences.ShowFloatingBall,
            applyFloatingBall: show =>
            {
                _preferences = _preferences with { ShowFloatingBall = show };
                SavePreferences();
                UpdateFloatingBall();
            },
            reduceMotion: _preferences.ReduceMotion,
            applyReduceMotion: reduce =>
            {
                _preferences = _preferences with { ReduceMotion = reduce };
                SavePreferences();

                // The ball builds its motion graph in the constructor, so rebuild
                // it to reflect the new preference (the settings Save() re-applies
                // the theme right after this).
                if (_floatingBall is not null)
                {
                    _floatingBall.Close();
                    _floatingBall = null;
                    UpdateFloatingBall();
                }
            },
            imported: () => _mainViewModel?.ReloadFromVault(),
            currentLanguage: _preferences.Language,
            applyLanguage: language =>
            {
                _preferences = _preferences with { Language = language };
                SavePreferences();
            },
            startupSupported: _startupRegistration.IsSupported,
            // The registry is the source of truth: the preference can point at a
            // moved/removed executable after an update or reinstall.
            autoStart: _startupRegistration.IsEnabled(ExecutablePath),
            startMinimized: _preferences.StartMinimized,
            applyAutoStart: enabled =>
            {
                if (!_startupRegistration.TrySetEnabled(enabled, ExecutablePath, TrayArgument))
                {
                    return false;
                }

                _preferences = _preferences with { AutoStart = enabled };
                SavePreferences();
                return true;
            },
            applyStartMinimized: minimized =>
            {
                _preferences = _preferences with { StartMinimized = minimized };
                SavePreferences();
            },
            closeBehavior: _preferences.CloseAction,
            applyCloseBehavior: behavior =>
            {
                _preferences = _preferences with { CloseAction = CloseActions.Normalize(behavior) };
                SavePreferences();
            },
            backupEnabled: _preferences.BackupEnabled,
            backupLocalEnabled: _preferences.LocalBackupEnabled,
            backupKeepCount: _preferences.BackupLocalKeepCount,
            backupIntervalMinutes: _preferences.BackupIntervalMinutes,
            backupLastSuccessUtc: _preferences.BackupLastSuccessUtc,
            backupLastResult: _preferences.BackupLastResult,
            applyBackup: enabled =>
            {
                _preferences = _preferences with { BackupEnabled = enabled };
                SavePreferences();
                _backupCoordinator?.Start();
            },
            backupNow: CreateBackupNowAsync,
            validateBackup: LocalBackupService.Validate,
            restoreBackup: path =>
            {
                if (_vault is null)
                {
                    return BackupResult.Failed("no-session", "The vault is locked.");
                }

                return new LocalBackupService(_vault.VaultPath).Restore(path, _vault.VaultPath);
            },
            listBackups: () => _vault is null ? [] : new LocalBackupService(_vault.VaultPath).ListBackups());
        var window = new SettingsWindow { DataContext = viewModel };
        // The native handle exists only after the dialog is shown.
        window.Opened += (_, _) => ApplyScreenGuard(window);
        _settingsWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, window))
            {
                _settingsWindow = null;
                SettingsBackupRefresh = null;
            }
        };

        SettingsBackupRefresh = (utc, result) => viewModel.RefreshBackupStatus(utc, result);
        _ = ObserveDialogAsync(window.ShowDialog(_mainWindow));
    }

    /// <summary>Fire-and-forget modal dialogs must not let exceptions escape.</summary>
    private static async Task ObserveDialogAsync(Task dialog)
    {
        try
        {
            await dialog;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Dialog failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Closes the settings dialog (lock/exit). It is modal on the main window,
    /// so leaving it open would let the user run commands against a session
    /// that has already been disposed.
    /// </summary>
    private void CloseSettingsWindow()
    {
        var window = _settingsWindow;
        if (window is null)
        {
            return;
        }

        _settingsWindow = null;
        try
        {
            window.Close();
        }
        catch (Exception)
        {
            // Closing is best effort; the guards in the view model stay in place.
        }
    }

    private void ApplyTheme(string theme)
    {
        _preferences = _preferences with { Theme = theme };
        SavePreferences();
        ApplyThemeVariant(theme);

        // Keep the toolbar icon in sync when the theme is changed elsewhere
        // (e.g. from the settings window).
        if (_mainViewModel is { } viewModel)
        {
            viewModel.ThemePreference = theme;
        }
    }

    /// <summary>
    /// Persists the language preference and offers an immediate restart, since
    /// the string tables are resolved once at startup.
    /// </summary>
    private async void ApplyLanguage(string language)
    {
        try
        {
            var changed = !string.Equals(_preferences.Language, language, StringComparison.Ordinal);
            _preferences = _preferences with { Language = language };
            SavePreferences();

            if (_mainViewModel is { } viewModel)
            {
                viewModel.LanguagePreference = language;
            }

            if (!changed || _mainWindow is not { } owner)
            {
                return;
            }

            var dialog = new ConfirmWindow(
                Loc.T("Main_LanguageSwitchTitle"),
                Loc.T("Main_LanguageSwitchMessage"),
                Loc.T("Main_RestartNow"),
                Loc.T("Main_RestartLater"));

            if (await dialog.ShowDialog<bool>(owner))
            {
                if (_restarting)
                {
                    return;
                }

                _restarting = true;
                ExitApplication(restart: true);
                if (!_exiting)
                {
                    // The replacement process could not be spawned (the current
                    // session kept running), so let the user try again.
                    _restarting = false;
                }
            }
            else if (_mainViewModel is { } current)
            {
                current.StatusMessage = Loc.T("Main_LanguageRestartHint");
            }
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Language switch failed: {exception.Message}");
        }
    }

    private void ApplyThemeVariant(string theme) => AppTheme.Apply(this, theme, _preferences.ReduceMotion);
}
