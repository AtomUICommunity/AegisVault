using System.Linq;
using AegisVault.App.Theme;
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
/// <para>
/// Smoothness comes from never touching layout while the pointer moves. The drop
/// marker is always present and only its <see cref="Visual.Opacity"/> is animated
/// (a composited property), so moving from row to row costs one property write
/// instead of a visibility toggle that re-runs arrange and paints the whole tree.
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
    /// <param name="MarkerRowId">
    /// Category whose row shows the insertion bar. It can be the row the pointer is over
    /// <em>or</em> its predecessor, because the bar is drawn on the top edge — so a drop
    /// below the last row still gets a line, on that last row's bottom half.
    /// </param>
    /// <param name="MarkerAtBottom">Draw the bar on the marker row's bottom edge.</param>
    private readonly record struct DropTarget(
        Guid? ParentId,
        Guid? BeforeId,
        bool Into,
        Guid? MarkerRowId = null,
        bool MarkerAtBottom = false);

    private readonly MainWindow _window;
    private readonly CategoryTreeRow _node;
    private readonly Border _row;
    private readonly Point _origin;
    private Border? _marker;
    private Border? _targetRow;
    private bool _started;

    /// <summary>The last marker we raised, so a move within the same band is a no-op.</summary>
    private Border? _raisedMarker;

    private CategoryDrag(MainWindow window, CategoryTreeRow node, Border row, Point origin)
    {
        _window = window;
        _node = node;
        _row = row;
        _origin = origin;
    }

    private Guid DraggedId => _node.Item.CategoryId!.Value;

    private MainViewModel? ViewModel => _window.DataContext as MainViewModel;

    /// <summary>True once the pointer has travelled far enough to count as a drag.</summary>
    public bool IsStarted => _started;

    /// <summary>
    /// Arms the gesture for a row press and captures the pointer to the window, so
    /// the whole gesture is delivered even outside the sidebar.
    /// </summary>
    public static CategoryDrag Arm(MainWindow window, CategoryTreeRow node, Border row, PointerPressedEventArgs e)
    {
        var gesture = new CategoryDrag(window, node, row, e.GetPosition(row));
        e.Pointer.Capture(window);
        return gesture;
    }

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

        var position = e.GetPosition(_window);

        if (!_started)
        {
            var local = e.GetPosition(_row);
            if (Math.Abs(local.X - _origin.X) < Threshold &&
                Math.Abs(local.Y - _origin.Y) < Threshold)
            {
                return false;
            }

            _started = true;
            _row.Classes.Add("dragSource");
            _window.ShowCategoryDragGhost(_node.Item.DisplayName, position);
            return true;
        }

        _window.ShowCategoryDragGhost(_node.Item.DisplayName, position);
        Mark(ResolveDropTarget(viewModel, position));
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

        var dropped = _started;
        var droppedId = DraggedId;
        Cancel();
        e.Pointer.Capture(null);

        if (target is not { } resolved || ViewModel is not { } current)
        {
            return;
        }

        // Capture where every row sits *before* the model changes, so the rows that
        // swap can slide from their old spot to the new one instead of teleporting.
        var before = dropped ? _window.CaptureCategoryRowPositions() : null;

        var error = current.DropCategory(droppedId, resolved.ParentId, resolved.BeforeId);
        if (error is not null)
        {
            current.StatusMessage = error;
            return;
        }

        if (before is not null)
        {
            _window.AnimateCategoryRowSwap(before);
        }
    }

    /// <summary>Abandons the gesture (capture lost, tree rebuilt, window closing).</summary>
    public void Cancel()
    {
        if (_raisedMarker is not null)
        {
            _raisedMarker.SetCurrentValue(Visual.OpacityProperty, 0d);
            _raisedMarker = null;
        }

        _marker = null;

        _targetRow?.Classes.Remove("dropTarget");
        _targetRow = null;

        _row.Classes.Remove("dragSource");
        _window.HideCategoryDragGhost();
    }

    /// <summary>Shows the marker for the row/gap the pointer is over.</summary>
    private void Mark(DropTarget? target)
    {
        if (target is not { } resolved || ViewModel is not { } viewModel)
        {
            if (_raisedMarker is not null)
            {
                _raisedMarker.SetCurrentValue(Visual.OpacityProperty, 0d);
                _raisedMarker = null;
            }

            _marker = null;
            _targetRow?.Classes.Remove("dropTarget");
            _targetRow = null;
            return;
        }

        if (resolved.Into && resolved.ParentId is { } parent)
        {
            // Re-parent: highlight the row that becomes the new parent.
            var row = RowFor(parent);
            if (!ReferenceEquals(row, _targetRow))
            {
                _targetRow?.Classes.Remove("dropTarget");
                _targetRow = row;
                _targetRow?.Classes.Add("dropTarget");
            }

            if (_raisedMarker is not null)
            {
                _raisedMarker.SetCurrentValue(Visual.OpacityProperty, 0d);
                _raisedMarker = null;
            }

            return;
        }

        _targetRow?.Classes.Remove("dropTarget");
        _targetRow = null;

        // The insertion bar lives on the marker row: its top edge for "insert above",
        // its bottom edge for "insert below / append".
        var marker = resolved.MarkerRowId is { } markerId && RowFor(markerId) is { } markerRow
            ? DropMarkerOf(markerRow)
            : null;

        if (marker is not null)
        {
            PositionMarker(marker, resolved.MarkerAtBottom);
        }

        if (ReferenceEquals(marker, _raisedMarker))
        {
            // Same bar in the same place: nothing to animate. This is what keeps a slow
            // drag from re-triggering work on every pixel.
            return;
        }

        if (_raisedMarker is not null && !ReferenceEquals(_raisedMarker, marker))
        {
            _raisedMarker.SetCurrentValue(Visual.OpacityProperty, 0d);
        }

        _raisedMarker = marker;
        marker?.SetCurrentValue(Visual.OpacityProperty, 1d);
        _marker = marker;
    }

    /// <summary>
    /// Puts the insertion bar on the marker row's top or bottom edge. Uses
    /// <see cref="Visual.RenderTransform"/> (composited) instead of a margin change, so
    /// moving the bar between rows never invalidates layout.
    /// </summary>
    private static void PositionMarker(Border marker, bool atBottom)
    {
        marker.VerticalAlignment = atBottom
            ? Avalonia.Layout.VerticalAlignment.Bottom
            : Avalonia.Layout.VerticalAlignment.Top;
    }

    /// <summary>
    /// Maps a point in window coordinates to a drop target.
    /// <para>
    /// The pointer row is split into three bands: the top quarter inserts above the row,
    /// the bottom quarter inserts below it, and the middle half drops <em>into</em> it.
    /// The bottom band is what makes "append at the end" visible — without it a drop
    /// below the last category showed no line at all.
    /// </para>
    /// <para>
    /// The uncategorized pseudo row has no category id, so it cannot be a re-parent
    /// target; it still participates as an <em>insertion reference</em>, which is why a
    /// category can now be dropped at the very end of the list.
    /// </para>
    /// </summary>
    private DropTarget? ResolveDropTarget(MainViewModel viewModel, Point position)
    {
        var rows = viewModel.CategoryRows
            .Select(row => (Row: row, Border: row.CategoryId is { } id ? RowFor(id) : FindRowFor(item: row)))
            .Where(entry => entry.Border is not null)
            .Select(entry => entry with { Border = entry.Border! })
            .ToList();

        for (var index = 0; index < rows.Count; index++)
        {
            var (node, border) = rows[index];
            if (border.TranslatePoint(new Point(0, 0), _window) is not { } top)
            {
                continue;
            }

            var height = border.Bounds.Height;
            if (height <= 0)
            {
                continue;
            }

            var bottom = top.Y + height;
            if (position.Y >= bottom)
            {
                continue;
            }

            var into = node.Item.CategoryId is { } id && node.IsUserCategory;

            if (position.Y < top.Y + (height / 4))
            {
                // Insert above this row: same parent, before it.
                if (into)
                {
                    return new DropTarget(node.Item.ParentId, node.Item.CategoryId, Into: false, node.Item.CategoryId);
                }

                // Pseudo row: insert before the previous real category (or at the end).
                return AppendAfter(index - 1);
            }

            if (position.Y >= bottom - (height / 4))
            {
                // Insert below this row: same parent, after it.
                if (into)
                {
                    return InsertAfter(index);
                }

                return AppendAfter(index - 1);
            }

            if (!into)
            {
                // Middle of the pseudo row: treat it as "append at the end".
                return AppendAfter(index - 1);
            }

            return viewModel.CanDropCategoryOn(DraggedId, node.Item.CategoryId!.Value)
                ? new DropTarget(node.Item.CategoryId, null, Into: true, node.Item.CategoryId)
                : null;
        }

        // Below every row: append at the end of the top level, with the bar drawn on the
        // bottom edge of the last realised row.
        return AppendAfter(rows.Count - 1);

        DropTarget? AppendAfter(int index)
        {
            // Walk back to the nearest row that has a category: the insertion point is
            // "after that category, under its parent".
            for (var cursor = index; cursor >= 0; cursor--)
            {
                var candidate = rows[cursor].Row;
                if (candidate.Item.CategoryId is { } id && candidate.IsUserCategory)
                {
                    return new DropTarget(candidate.Item.ParentId, null, Into: false, id, MarkerAtBottom: true);
                }
            }

            return null;
        }

        DropTarget? InsertAfter(int index)
        {
            var node = rows[index].Row;
            if (node.Item.CategoryId is not { } id)
            {
                return AppendAfter(index - 1);
            }

            // The next sibling under the same parent; null means "append".
            var siblings = viewModel.CategoryRows
                .Where(row => row.Item.ParentId == node.Item.ParentId && row.Item.CategoryId is not null)
                .ToList();
            var position = siblings.FindIndex(row => row.Item.CategoryId == id);
            var next = position >= 0 && position + 1 < siblings.Count
                ? siblings[position + 1].Item.CategoryId
                : null;

            return new DropTarget(node.Item.ParentId, next, Into: false, id, MarkerAtBottom: true);
        }
    }

    /// <summary>The realised row for a tree row that has no category id (uncategorized).</summary>
    private Border? FindRowFor(CategoryTreeRow item)
        => _window.GetCategoryRows()
            .FirstOrDefault(candidate => ReferenceEquals(candidate.DataContext, item));

    private Border? RowFor(Guid categoryId)
        => _window.FindCategoryRow(categoryId);

    private static Border? DropMarkerOf(Border row)
        => row.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(candidate => candidate.Name == "CategoryDropBefore");
}
