namespace AegisVault.Core.Models;

/// <summary>
/// User-defined category (single assignment per entry), stored encrypted inside
/// the vault alongside the entries. Categories form a tree: <see cref="ParentId"/>
/// is <c>null</c> for a top-level category. Tags remain the multi-assignment
/// dimension.
/// </summary>
public sealed record Category
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Palette key ("red", "blue", …) or a literal "#RRGGBB" for custom colours;
    /// empty means "unset" and the UI falls back to a name-derived colour.
    /// </summary>
    public string Color { get; init; } = string.Empty;

    /// <summary>
    /// Parent category, or <c>null</c> for a top-level category. Load-time
    /// normalization drops dangling references, breaks cycles and caps depth.
    /// </summary>
    public Guid? ParentId { get; init; }

    /// <summary>
    /// Position among its siblings (ascending). The user owns this order by
    /// dragging rows in the sidebar; <c>0</c> means "never ordered" and is
    /// resolved to the alphabetical position at load time (categories payload
    /// v3 and older carried no order at all).
    /// </summary>
    public int SortOrder { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
