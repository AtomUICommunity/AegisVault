using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using AegisVault.App.Services;

namespace AegisVault.App.Views;

/// <summary>
/// Optional always-on-top floating ball: click opens quick access, drag moves it
/// around the screen and dropping it near a vertical screen edge docks it as a
/// half-visible ball that slides fully out on hover.
/// <para>
/// Dragging is handled manually because a <see cref="Button"/> would swallow the
/// pointer events (see pitfall P-38) and the platform move loop offers no way to
/// tell a click from a drag.
/// </para>
/// </summary>
public partial class FloatingBallWindow : Window
{
    private const int DragThreshold = 4;
    private const int DockThreshold = 40;

    private static readonly TransformOperations RingRest = TransformOperations.Parse("scale(1)");
    private static readonly TransformOperations PulsePeak = TransformOperations.Parse("scale(1.22)");
    private static readonly TransformOperations RipplePeak = TransformOperations.Parse("scale(1.35)");
    private static readonly TransformOperations ClusterRest = TransformOperations.Parse("translate(0px, 0px)");

    private readonly Transitions _pulseTransitions =
    [
        new TransformOperationsTransition
        {
            Property = Visual.RenderTransformProperty,
            Duration = TimeSpan.FromSeconds(1.6),
            Easing = new SineEaseOut(),
        },
        new DoubleTransition
        {
            Property = Visual.OpacityProperty,
            Duration = TimeSpan.FromSeconds(1.6),
            Easing = new SineEaseOut(),
        },
    ];

    private readonly Transitions _rippleTransitions =
    [
        new TransformOperationsTransition
        {
            Property = Visual.RenderTransformProperty,
            Duration = TimeSpan.FromMilliseconds(260),
            Easing = new CubicEaseOut(),
        },
        new DoubleTransition
        {
            Property = Visual.OpacityProperty,
            Duration = TimeSpan.FromMilliseconds(260),
            Easing = new CubicEaseOut(),
        },
    ];

    private readonly Transitions _clusterTransitions =
    [
        new TransformOperationsTransition
        {
            Property = Visual.RenderTransformProperty,
            Duration = TimeSpan.FromMilliseconds(180),
            Easing = new CubicEaseOut(),
        },
    ];

    /// <summary>Raised on a plain click (no drag).</summary>
    public event Action? BallClicked;

    /// <summary>Raised after the ball settles; carries the window position and dock side.</summary>
    public event Action<PixelPoint, string?>? PlacementChanged;

    private PixelPoint _pressScreen;
    private PixelPoint _pressWindow;
    private bool _dragging;
    private bool _moved;
    private bool _revealed;
    private readonly bool _reducedMotion;
    private string? _dockedSide;
    private DispatcherTimer? _pulseTimer;
    private DispatcherTimer? _breathTimer;
    private DateTime _revealGraceUntil;

    public FloatingBallWindow()
        : this(reducedMotion: false)
    {
    }

    public FloatingBallWindow(bool reducedMotion)
    {
        InitializeComponent();

        _reducedMotion = reducedMotion;
        BallRoot.Transitions = reducedMotion ? null : _clusterTransitions;

        if (reducedMotion)
        {
            BallSurface.Classes.Add("reduced");
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (_reducedMotion)
        {
            // No entrance fade and no idle animation.
            BallSurface.Classes.Remove("entering");
            return;
        }

        // Let the first frame lay out the "entering" state, then transition in.
        DispatcherTimer.RunOnce(
            () =>
            {
                BallSurface.Classes.Remove("entering");
                StartPulse();
                StartBreath();
            },
            TimeSpan.FromMilliseconds(40));
    }

    /// <summary>Restores a persisted placement, clamped into a visible work area.</summary>
    public void ApplyPlacement(PixelPoint desired, string? dockedSide)
    {
        var side = BallPlacement.NormalizeSide(dockedSide);
        if (side is null || !TryGetScreenFor(desired, out var area, out var size))
        {
            Position = desired;
            if (side is not null)
            {
                _dockedSide = side;
                BallRoot.Classes.Add(side == BallPlacement.DockLeft ? "dockleft" : "dockright");
            }

            return;
        }

        if (side is null)
        {
            Position = BallPlacement.Clamp(desired, area, size.Width, size.Height);
            return;
        }

        _dockedSide = side;
        _revealed = false;
        BallRoot.Classes.Add(side == BallPlacement.DockLeft ? "dockleft" : "dockright");
        var y = Math.Clamp(desired.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height));
        Position = side == BallPlacement.DockLeft
            ? new PixelPoint(area.X - (size.Width / 2), y)
            : new PixelPoint(area.Right - (size.Width / 2), y);
    }

    private bool TryGetScreenFor(PixelPoint point, out PixelRect area, out PixelSize size)
    {
        try
        {
            var screen = Screens.ScreenFromPoint(point) ?? Screens.Primary;
            if (screen is not null)
            {
                area = screen.WorkingArea;
                size = PhysicalSize(screen.Scaling);
                return true;
            }
        }
        catch (Exception)
        {
            // Headless/unsupported platforms: keep the requested position as-is.
        }

        area = default;
        size = default;
        return false;
    }

    private void OnBallPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Pressing a revealed docked ball collapses it again so the drag starts
        // from the canonical edge position.
        Collapse();

        _pressScreen = this.PointToScreen(e.GetPosition(this));
        _pressWindow = Position;
        _dragging = true;
        _moved = false;
        BallSurface.Classes.Add("pressed");
        StopPulse();
        StopBreath();
        e.Pointer.Capture(BallSurface);
        e.Handled = true;
    }

    private void OnBallPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var current = this.PointToScreen(e.GetPosition(this));
        var dx = current.X - _pressScreen.X;
        var dy = current.Y - _pressScreen.Y;
        if (Math.Abs(dx) + Math.Abs(dy) < DragThreshold)
        {
            return;
        }

        _moved = true;
        if (!BallSurface.Classes.Contains("dragging"))
        {
            BallSurface.Classes.Add("dragging");
        }

        Position = new PixelPoint(_pressWindow.X + dx, _pressWindow.Y + dy);
        e.Handled = true;
    }

    private void OnBallPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        BallSurface.Classes.Remove("pressed");
        BallSurface.Classes.Remove("dragging");
        e.Pointer.Capture(null);

        if (_moved)
        {
            SettleAfterDrag();
        }
        else if (_dockedSide is not null && !_revealed)
        {
            // A docked ball expands on tap instead of opening quick access.
            PlayRipple();
            PlayBounce();
            Reveal();
        }
        else
        {
            PlayRipple();
            PlayBounce();
            BallClicked?.Invoke();
        }

        StartPulse();
        StartBreath();
        e.Handled = true;
    }

    private void OnBallPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _dragging = false;
        BallSurface.Classes.Remove("pressed");
        BallSurface.Classes.Remove("dragging");
        StartPulse();
        StartBreath();
    }

    private void OnBallPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_dockedSide is not null)
        {
            Reveal();
        }
    }

    private void OnBallPointerExited(object? sender, PointerEventArgs e)
    {
        // Ignore the exit that follows a reveal: the ball slides out from under
        // the pointer, which would otherwise fold it straight back.
        if (_dockedSide is not null && !_dragging && DateTime.UtcNow >= _revealGraceUntil)
        {
            Collapse();
        }
    }

    private void SettleAfterDrag()
    {
        if (!TryGetWorkArea(out var area, out var size))
        {
            ClearDock();
            PlacementChanged?.Invoke(Position, null);
            return;
        }

        var leftGap = Position.X - area.X;
        var rightGap = area.Right - (Position.X + size.Width);

        if (leftGap <= DockThreshold)
        {
            Dock(BallPlacement.DockLeft, area, size);
        }
        else if (rightGap <= DockThreshold)
        {
            Dock(BallPlacement.DockRight, area, size);
        }
        else
        {
            ClearDock();
            Position = BallPlacement.Clamp(Position, area, size.Width, size.Height);
        }

        PlacementChanged?.Invoke(Position, _dockedSide);
    }

    /// <summary>Work area and physical size of the screen holding the ball (best effort).</summary>
    private bool TryGetWorkArea(out PixelRect area, out PixelSize size)
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen is not null)
            {
                area = screen.WorkingArea;
                size = PhysicalSize(screen.Scaling);
                return true;
            }
        }
        catch (Exception)
        {
            // Headless/unsupported platforms: fall back to free positioning.
        }

        area = default;
        size = default;
        return false;
    }

    private void Dock(string side, PixelRect area, PixelSize size)
    {
        ClearDock();
        _dockedSide = side;
        BallRoot.Classes.Add(side == BallPlacement.DockLeft ? "dockleft" : "dockright");
        var y = Math.Clamp(Position.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height));
        Position = side == BallPlacement.DockLeft
            ? new PixelPoint(area.X - (size.Width / 2), y)
            : new PixelPoint(area.Right - (size.Width / 2), y);
    }

    private void ClearDock()
    {
        if (_dockedSide is not null)
        {
            // Normalise the window back to the collapsed anchor before undocking.
            Collapse(moveWindow: true);
        }

        _dockedSide = null;
        _revealed = false;
        BallRoot.Classes.Remove("dockleft");
        BallRoot.Classes.Remove("dockright");
        BallRoot.Classes.Remove("revealed");
        BallRoot.RenderTransform = ClusterRest;
    }

    private void Reveal()
    {
        if (_revealed || _dockedSide is null)
        {
            return;
        }

        _revealed = true;
        _revealGraceUntil = DateTime.UtcNow.AddMilliseconds(400);
        BallRoot.Classes.Add("revealed");

        // The window glides half a width back on screen while the cluster is
        // translated the opposite way first, so the ball appears to slide out
        // of the edge instead of jumping.
        var offset = DockOffset;
        var dx = _dockedSide == BallPlacement.DockLeft ? offset : -offset;
        Position = new PixelPoint(Position.X + dx, Position.Y);
        SlideCluster(-dx);
    }

    private void Collapse() => Collapse(moveWindow: true);

    private void Collapse(bool moveWindow)
    {
        if (!_revealed)
        {
            return;
        }

        _revealed = false;
        BallRoot.Classes.Remove("revealed");

        if (!moveWindow || _dockedSide is null)
        {
            BallRoot.RenderTransform = ClusterRest;
            return;
        }

        var offset = DockOffset;
        var dx = _dockedSide == BallPlacement.DockLeft ? -offset : offset;
        Position = new PixelPoint(Position.X + dx, Position.Y);
        SlideCluster(-dx);
    }

    /// <summary>Instantly offsets the cluster, then animates the offset away.</summary>
    private void SlideCluster(int compensation)
    {
        if (_reducedMotion)
        {
            BallRoot.RenderTransform = ClusterRest;
            return;
        }

        BallRoot.Transitions = null;
        BallRoot.RenderTransform = TransformOperations.Parse($"translate({compensation}px, 0px)");
        DispatcherTimer.RunOnce(
            () =>
            {
                BallRoot.Transitions = _clusterTransitions;
                BallRoot.RenderTransform = ClusterRest;
            },
            TimeSpan.FromMilliseconds(30));
    }

    private int DockOffset => (int)Math.Round(Width / 2);

    /// <summary>Two quick expanding rings on click (driven by Transitions).</summary>
    private void PlayRipple()
    {
        if (_reducedMotion)
        {
            return;
        }

        FireRipple();
        DispatcherTimer.RunOnce(FireRipple, TimeSpan.FromMilliseconds(150));
    }

    private void FireRipple()
    {
        RippleRing.Transitions = null;
        RippleRing.RenderTransform = RingRest;
        RippleRing.Opacity = 0.85;

        // A real timer tick guarantees a rendered frame between the reset and the
        // animation, otherwise the two states coalesce and nothing animates.
        DispatcherTimer.RunOnce(
            () =>
            {
                RippleRing.Transitions = _rippleTransitions;
                RippleRing.RenderTransform = RipplePeak;
                RippleRing.Opacity = 0;
            },
            TimeSpan.FromMilliseconds(30));
    }

    /// <summary>Slow breathing ring while the ball is idle.</summary>
    private void StartPulse()
    {
        if (_reducedMotion)
        {
            return;
        }

        if (_pulseTimer is null)
        {
            _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
            _pulseTimer.Tick += (_, _) => FirePulse();
        }

        if (!_pulseTimer.IsEnabled)
        {
            _pulseTimer.Start();
            FirePulse();
        }
    }

    private void StopPulse()
    {
        _pulseTimer?.Stop();
        PulseRing.Transitions = null;
        PulseRing.Opacity = 0;
    }

    private void FirePulse()
    {
        PulseRing.Transitions = null;
        PulseRing.RenderTransform = RingRest;
        PulseRing.Opacity = 0.45;

        DispatcherTimer.RunOnce(
            () =>
            {
                PulseRing.Transitions = _pulseTransitions;
                PulseRing.RenderTransform = PulsePeak;
                PulseRing.Opacity = 0;
            },
            TimeSpan.FromMilliseconds(30));
    }

    /// <summary>Slow breathing of the ball itself while it is idle.</summary>
    private void StartBreath()
    {
        if (_reducedMotion)
        {
            return;
        }

        if (_breathTimer is null)
        {
            _breathTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            _breathTimer.Tick += (_, _) =>
            {
                if (!BallSurface.Classes.Contains("breath"))
                {
                    BallSurface.Classes.Add("breath");
                }
                else
                {
                    BallSurface.Classes.Remove("breath");
                }
            };
        }

        if (!_breathTimer.IsEnabled)
        {
            _breathTimer.Start();
        }
    }

    private void StopBreath()
    {
        _breathTimer?.Stop();
        BallSurface.Classes.Remove("breath");
    }

    /// <summary>Short overshoot after a click so the tap feels acknowledged.</summary>
    private void PlayBounce()
    {
        if (_reducedMotion)
        {
            return;
        }

        BallSurface.Classes.Add("bouncing");
        DispatcherTimer.RunOnce(
            () => BallSurface.Classes.Remove("bouncing"),
            TimeSpan.FromMilliseconds(220));
    }

    private PixelSize PhysicalSize(double scaling)
    {
        var scale = scaling <= 0 ? 1.0 : scaling;
        return new PixelSize(
            Math.Max(1, (int)Math.Round(Width * scale)),
            Math.Max(1, (int)Math.Round(Height * scale)));
    }
}
