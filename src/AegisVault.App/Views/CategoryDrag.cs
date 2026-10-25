using System.Linq;
using AegisVault.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace AegisVault.App.Views;

/// <summary>
/// Self-drawn drag-and-drop for the category sidebar.
/// <para>
/// AtomUI's <c>NavMenu</c> has no drag API at all (no Drag*/Drop*/Reorder* member on
/// NavMenu, NavMenuItem or the inline row header), so the gesture is entirely ours.
/// The row <c>Border</c> marks <c>PointerPressed</c> as handled to keep the menu's
/// row activation out of the way, and this type takes over from there.
/// </para>
/// <para>
/// The pointer is captured to the <em>window</em>, not the row: on a real desktop
/// the menu's interaction handler re-captures the pointer to its own container right
/// after the press, so a row-scoped capture loses every subsequent move event. That
/// does not happen on the headless platform, which is why a row-scoped version can
/// pass its tests and still do nothing on a real machine.
/// </para>
/// </summary>
internal sealed class CategoryDrag
{
    /// <summary>Movement (in device-independent pixels) before a press becomes a drag.</summary>
    private const double Threshold = 4;

    /// <summary>Where a drop would land.</summary>
    /// <param name="ParentId">Parent the moved category ends up under.</param>
    /// <param name="BeforeId">Sibling it is inserted before, or null to append.</param>
    /// <param name="Into">True when the pointer is over a row (re-parent).</param>
    private readonly record struct DropTarget(Guid? ParentId, Guid? BeforeId, bool Into);

    private readonly MainWindow _window;
    private readonly CategoryNavNode _node;
    private readonly Border _row;
    private readonly Point _origin;
    private Border? _marker;
    private Border? _targetRow;
    private bool _started;

    private CategoryDrag(MainWindow window, CategoryNavNode node, Border row, Point origin)
    {
        _window = window;
        _node = node;
        _row = row;
        _origin = origin;
    }

    /// <summary>
    /// Arms the gesture for a row press and captures the pointer to the window, so
    /// the whole gesture is delivered even outside the sidebar.
    /// </summary>
    public static CategoryDrag Arm(MainWindow window, CategoryNavNode node, Border row, PointerPressedEventArgs e)
    {
        var gesture = new CategoryDrag(window, node, row, e.GetPosition(row));
        e.Pointer.Capture(window);
        return gesture;
    }

    private Guid DraggedId => _node.Item.CategoryId!.Value;

    private MainViewModel? ViewModel => _window.DataContext as MainViewModel;

    /// <summary>True once the pointer has travelled far enough to count as a drag.</summary>
    public bool IsStarted => _started;

    /// <summary>
    /// Tracks the pointer. Returns true for a started drag so the caller can stop
    /// treating the gesture as an ordinary click.
    /// </summary>
    public bool Track(PointerEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return false;
        }

        if (!_started)
        {
            var position = e.GetPosition(_row);
            if (Math.Abs(position.X - _origin.X) < Threshold &&
                Math.Abs(position.Y - _origin.Y) < Threshold)
            {
                return false;
            }

            _started = true;
            _row.Classes.Add("dragSource");
        }

        Mark(ResolveDropTarget(viewModel, e.GetPosition(_window)));
        return true;
    }

    /// <summary>
    /// Finishes the gesture and applies the drop. A press that never travelled past
    /// the threshold applies nothing, so a plain click keeps its selection behaviour.
    /// </summary>
    public void Release(PointerReleasedEventArgs e)
    {
        var target = _started && ViewModel is { } viewModel
            ? ResolveDropTarget(viewModel, e.GetPosition(_window))
            : (DropTarget?)null;

        Cancel();
        e.Pointer.Capture(null);

        if (target is not { } resolved || ViewModel is not { } current)
        {
            return;
        }

        var error = current.DropCategory(DraggedId, resolved.ParentId, resolved.BeforeId);
        if (error is not null)
        {
            current.StatusMessage = error;
        }
    }

    /// <summary>Abandons the gesture (capture lost, tree rebuilt, window closing).</summary>
    public void Cancel()
    {
        _marker?.SetCurrentValue(Visual.IsVisibleProperty, false);
        _marker = null;

        _targetRow?.Classes.Remove("dropTarget");
        _targetRow = null;

        _row.Classes.Remove("dragSource");
    }

    /// <summary>Shows the marker for the row/gap the pointer is over.</summary>
    private void Mark(DropTarget? target)
    {
        _marker?.SetCurrentValue(Visual.IsVisibleProperty, false);
        _marker = null;

        _targetRow?.Classes.Remove("dropTarget");
        _targetRow = null;

        if (target is not { } resolved || ViewModel is not { } viewModel)
        {
            return;
        }

        if (resolved.Into && resolved.ParentId is { } parent)
        {
            if (RowFor(parent) is { } row)
            {
                row.Classes.Add("dropTarget");
                _targetRow = row;
            }

            return;
        }

        if (resolved.BeforeId is { } before && RowFor(before) is { } markerRow &&
            DropMarkerOf(markerRow) is { } marker)
        {
            marker.SetCurrentValue(Visual.IsVisibleProperty, true);
            _marker = marker;
        }
    }

    /// <summary>
    /// Maps a point in window coordinates to a drop target. The top quarter of a row
    /// inserts above it (also the only way to drop at the very top of the tree), the
    /// rest of the row drops into it.
    /// </summary>
    private DropTarget? ResolveDropTarget(MainViewModel viewModel, Point position)
    {
        foreach (var node in viewModel.CategoryNodes.SelectMany(Flatten))
        {
            if (node.Item.CategoryId is not { } id || !node.IsUserCategory)
            {
                continue;
            }

            if (RowFor(id) is not { } row ||
                row.TranslatePoint(new Point(0, 0), _window) is not { } top)
            {
                continue;
            }

            if (position.Y < top.Y)
            {
                return new DropTarget(node.Item.ParentId, id, Into: false);
            }

            if (position.Y <= top.Y + row.Bounds.Height)
            {
                if (position.Y <= top.Y + row.Bounds.Height / 4)
                {
                    return new DropTarget(node.Item.ParentId, id, Into: false);
                }

                return viewModel.CanDropCategoryOn(DraggedId, id)
                    ? new DropTarget(id, null, Into: true)
                    : null;
            }
        }

        // Below the last row: append to the top level.
        return new DropTarget(null, null, Into: false);

        static IEnumerable<CategoryNavNode> Flatten(CategoryNavNode node)
        {
            yield return node;

            foreach (var child in node.Entries.OfType<CategoryNavNode>().SelectMany(Flatten))
            {
                yield return child;
            }
        }
    }

    private Border? RowFor(Guid categoryId)
        => _window.GetCategoryRows()
            .FirstOrDefault(candidate => candidate.DataContext is CategoryNavNode node &&
                                         node.Item.CategoryId == categoryId);

    private static Border? DropMarkerOf(Border row)
        => row.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(candidate => candidate.Name == "CategoryDropBefore");
}
