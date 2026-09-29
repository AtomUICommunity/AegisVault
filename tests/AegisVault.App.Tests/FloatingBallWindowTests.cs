using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using AegisVault.App.Views;
using Xunit;

namespace AegisVault.App.Tests;

/// <summary>
/// The floating ball used to swallow every pointer event (a Button marked
/// PointerPressed as handled), so click and drag silently did nothing.
/// </summary>
public sealed class FloatingBallWindowTests
{
    private static readonly Point BallCentre = new(26, 26);

    [Fact]
    public Task ClickRaisesBallClicked() => Headless.Run(() =>
    {
        var window = new FloatingBallWindow();
        try
        {
            window.Show();
            var raised = 0;
            window.BallClicked += () => raised++;

            window.MouseDown(BallCentre, MouseButton.Left);
            window.MouseUp(BallCentre, MouseButton.Left);

            Assert.Equal(1, raised);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task DragMovesTheWindowAndDoesNotClick() => Headless.Run(() =>
    {
        var window = new FloatingBallWindow();
        try
        {
            window.Show();
            var raised = 0;
            window.BallClicked += () => raised++;

            window.MouseDown(BallCentre, MouseButton.Left);
            window.MouseMove(new Point(BallCentre.X + 60, BallCentre.Y + 40));
            window.MouseUp(new Point(BallCentre.X + 60, BallCentre.Y + 40), MouseButton.Left);

            Assert.Equal(0, raised);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TinyJitterStillCountsAsClick() => Headless.Run(() =>
    {
        var window = new FloatingBallWindow();
        try
        {
            window.Show();
            var raised = 0;
            window.BallClicked += () => raised++;

            window.MouseDown(BallCentre, MouseButton.Left);
            window.MouseMove(new Point(BallCentre.X + 1, BallCentre.Y + 1));
            window.MouseUp(new Point(BallCentre.X + 1, BallCentre.Y + 1), MouseButton.Left);

            Assert.Equal(1, raised);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task PressAndDragToggleTheirVisualClasses() => Headless.Run(() =>
    {
        var window = new FloatingBallWindow();
        try
        {
            window.Show();
            var ball = window.FindControl<Border>("BallSurface");
            Assert.NotNull(ball);

            window.MouseDown(BallCentre, MouseButton.Left);
            Assert.Contains("pressed", ball!.Classes);

            window.MouseMove(new Point(BallCentre.X + 40, BallCentre.Y + 24));
            Assert.Contains("dragging", ball.Classes);

            window.MouseUp(new Point(BallCentre.X + 40, BallCentre.Y + 24), MouseButton.Left);
            Assert.DoesNotContain("pressed", ball.Classes);
            Assert.DoesNotContain("dragging", ball.Classes);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task DockedBallRevealsOnHoverAndMarksItselfDocked() => Headless.Run(() =>
    {
        var window = new FloatingBallWindow();
        try
        {
            window.Show();
            var root = window.FindControl<Panel>("BallRoot");
            Assert.NotNull(root);

            window.ApplyPlacement(new PixelPoint(0, 300), "left");

            Assert.Contains("dockleft", root!.Classes);
            Assert.DoesNotContain("revealed", root.Classes);

            window.MouseMove(BallCentre);   // hover the visible half
            Assert.Contains("revealed", root.Classes);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ReducedMotionDisablesTheEntranceAndIdleAnimations() => Headless.Run(() =>
    {
        var window = new FloatingBallWindow(reducedMotion: true);
        try
        {
            window.Show();

            var ball = window.FindControl<Border>("BallSurface");
            var root = window.FindControl<Panel>("BallRoot");
            Assert.NotNull(ball);
            Assert.NotNull(root);

            // BallRoot keeps a transition for the docking glide only when motion
            // is enabled; here it must stay null.
            Assert.Null(root!.Transitions);
            Assert.Contains("reduced", ball!.Classes);
            Assert.DoesNotContain("entering", ball.Classes);
            Assert.DoesNotContain("breath", ball.Classes);
        }
        finally
        {
            window.Close();
        }
    });
}
