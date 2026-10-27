using AegisVault.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AegisVault.App.ViewModels;

/// <summary>
/// One visible row of the sidebar category tree.
/// <para>
/// The tree is rendered as a <em>flat</em> list: <see cref="MainViewModel.CategoryRows"/>
/// holds exactly the rows that are currently visible, in display order, and
/// <see cref="Depth"/> drives the indent. Keeping the list flat is what makes this
/// sidebar immune to the AtomUI limitation that forced re-parent drags to rebuild the
/// whole tree: a tree control only creates child row containers for the children that
/// already exist when its parent container is created, so a branch attached at runtime
/// stayed collapsed forever (no programmatic expand API exists). A flat list has no
/// parent containers to get wrong — expanding or collapsing simply inserts or removes
/// rows.
/// </para>
/// </summary>
public sealed partial class CategoryTreeRow : ObservableObject
{
    public CategoryTreeRow(CategoryItem item, int depth, bool hasChildren, bool isExpanded)
    {
        Item = item;
        Depth = depth;
        HasChildren = hasChildren;
        this.isExpanded = isExpanded;
    }

    /// <summary>Row data (name, count, colour, category id, …).</summary>
    public CategoryItem Item { get; private set; }

    /// <summary>1-based nesting level; the row indent is derived from it.</summary>
    public int Depth { get; private set; }

    /// <summary>True when this category has at least one child category.</summary>
    public bool HasChildren { get; private set; }

    [ObservableProperty]
    private bool isExpanded;

    public Guid? CategoryId => Item.CategoryId;

    public string Key => Item.Key;

    /// <summary>True for a real category row; the "uncategorized" pseudo row reports false.</summary>
    public bool IsUserCategory => Item.IsUserCategory && Item.CategoryId is not null;

    /// <summary>Replaces the row data in place, so the row keeps its expansion state.</summary>
    public void Update(CategoryItem item, bool hasChildren)
    {
        if (HasChildren != hasChildren)
        {
            HasChildren = hasChildren;
            OnPropertyChanged(nameof(HasChildren));
        }

        if (Item == item)
        {
            return;
        }

        Item = item;
        OnPropertyChanged(nameof(Item));
    }

    /// <summary>Moves the row to a new nesting level (a re-parent changes its depth).</summary>
    public void SetDepth(int depth)
    {
        if (Depth == depth)
        {
            return;
        }

        Depth = depth;
        OnPropertyChanged(nameof(Depth));
    }
}
