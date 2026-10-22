using AegisVault.Core.Models;
using AegisVault.Core.Services;

namespace AegisVault.Core.Storage;

/// <summary>
/// Load-time normalization and version upgrades for decrypted payloads.
/// <para>
/// Source-generated <c>System.Text.Json</c> contexts do <b>not</b> apply property
/// initializers for collection/object properties that are missing from older
/// payloads (they deserialize as <c>null</c>, unlike the reflection-based
/// serializer). Every model that can be loaded from disk therefore passes
/// through <see cref="Normalize(PasswordEntry)"/> and friends, and version-specific
/// changes are applied by <see cref="UpgradeEntry"/>.
/// </para>
/// <para>
/// Rules for future changes (see <c>docs/数据迁移设计.md</c>):
/// add-field: bump the payload version + extend <see cref="UpgradeEntry"/> +
/// extend <see cref="Normalize(PasswordEntry)"/> + extend the contract test.
/// </para>
/// </summary>
internal static class ModelMigrations
{
    /// <summary>Oldest entry payload version this application can still read.</summary>
    public const int OldestSupportedEntryVersion = 1;

    /// <summary>
    /// Applies version-specific upgrades to a decrypted entry payload.
    /// Always normalizes afterwards so consumers can rely on non-null members.
    /// </summary>
    public static PasswordEntry UpgradeEntry(PasswordEntry entry, int fromVersion)
    {
        if (fromVersion < OldestSupportedEntryVersion)
        {
            throw new InvalidDataException($"Entry format version {fromVersion} is not supported.");
        }

        // v1 -> v2: collection members (Tags/Urls/CustomFields) may be missing
        // from the payload; v2 guarantees non-null collections.
        // v2 -> v3: added scalar credential fields (Phone/AppId/Secret/ApiKey);
        // older payloads simply lack them and normalize to empty strings.
        // v3 -> v4: added the scalar Email field; same story.
        // v4 -> v5: added the nullable DeletedAt/LastOpenedAt fields. Older
        // payloads lack them, so they deserialize as null ("live"/"never
        // opened"); the branch is written out explicitly because the migration
        // rules forbid relying on implicit behaviour.
        // v5 -> v6: added PasswordHistory (nullable). Older payloads have no
        // history to backfill, so it normalises to an empty list.
        if (fromVersion < 5)
        {
            entry = entry with { DeletedAt = null, LastOpenedAt = null };
        }

        if (fromVersion < 6)
        {
            entry = entry with { PasswordHistory = [] };
        }

        return Normalize(entry);
    }

    /// <summary>Fills in defaults for members that older payloads may lack.</summary>
    public static PasswordEntry Normalize(PasswordEntry entry) => entry with
    {
        Title = entry.Title ?? string.Empty,
        Username = entry.Username ?? string.Empty,
        Password = entry.Password ?? string.Empty,
        Url = entry.Url ?? string.Empty,
        Notes = entry.Notes ?? string.Empty,
        TotpSecret = entry.TotpSecret ?? string.Empty,
        Phone = entry.Phone ?? string.Empty,
        Email = entry.Email ?? string.Empty,
        AppId = entry.AppId ?? string.Empty,
        Secret = entry.Secret ?? string.Empty,
        ApiKey = entry.ApiKey ?? string.Empty,
        Tags = entry.Tags is null ? [] : entry.Tags,
        Urls = entry.Urls is null ? [] : entry.Urls,
        CustomFields = entry.CustomFields is null ? [] : entry.CustomFields,
        // Nullable timestamps are valid as null; a default (0001-01-01) value is
        // never written by this application, so treat it as "not set" defensively.
        DeletedAt = entry.DeletedAt == default(DateTimeOffset) ? null : entry.DeletedAt,
        LastOpenedAt = entry.LastOpenedAt == default(DateTimeOffset) ? null : entry.LastOpenedAt,
        PasswordHistory = NormalizePasswordHistory(entry.PasswordHistory),
    };

    /// <summary>
    /// Trims the history to the retention cap, drops blank/corrupt entries and
    /// forces newest-first order. A missing list (older payload) becomes empty.
    /// </summary>
    private static List<PasswordHistoryItem> NormalizePasswordHistory(IReadOnlyList<PasswordHistoryItem>? history)
    {
        if (history is null || history.Count == 0)
        {
            return [];
        }

        return history
            .Where(item => item is not null
                && !string.IsNullOrEmpty(item.Password)
                && item.ChangedAt != default(DateTimeOffset))
            .OrderByDescending(item => item.ChangedAt)
            .Take(PasswordEntry.MaximumPasswordHistory)
            .ToList();
    }

    /// <summary>Fills in defaults for members that older payloads may lack.</summary>
    public static UserConfig Normalize(UserConfig config) => config with
    {
        Generator = config.Generator is null ? new PasswordGeneratorOptions() : config.Generator,
    };

    /// <summary>Fills in defaults for members that older payloads may lack.</summary>
    public static Category Normalize(Category category) => category with
    {
        Name = category.Name ?? string.Empty,
        Color = CategoryColors.Normalize(category.Color),
    };

    /// <summary>Deepest category level the UI renders (roots sit at level 1).</summary>
    public const int MaxCategoryDepth = 4;

    /// <summary>
    /// Applies version-specific upgrades to a decrypted categories payload and
    /// normalizes the tree. Called for the envelope (v3) and for the bare lists
    /// written by older releases (v1/v2).
    /// </summary>
    public static List<Category> UpgradeCategories(IReadOnlyList<Category>? categories, int fromVersion)
    {
        if (fromVersion < CategoriesPayload.OldestSupportedVersion)
        {
            throw new InvalidDataException($"Categories payload version {fromVersion} is not supported.");
        }

        if (fromVersion > CategoriesPayload.CurrentVersion)
        {
            throw new UnsupportedVaultVersionException(
                $"Categories payload version {fromVersion} is newer than this application supports.");
        }

        // v1 -> v2: added the colour field (normalized to an empty string).
        // v2 -> v3: added ParentId (hierarchy); older payloads are flat, so every
        // category becomes a root.
        return NormalizeCategoryTree(categories);
    }

    /// <summary>
    /// Normalizes a category list into a well-formed tree. A category must never
    /// be lost: dangling parents, self-parents and cycles become roots, anything
    /// deeper than <see cref="MaxCategoryDepth"/> is lifted to the deepest allowed
    /// level, and duplicate sibling names are renamed ("Name (2)") because two
    /// categories under the same parent must stay distinguishable.
    /// </summary>
    public static List<Category> NormalizeCategoryTree(IReadOnlyList<Category>? categories)
    {
        var normalized = new List<Category>();
        var byId = new Dictionary<Guid, Category>();
        foreach (var candidate in categories ?? [])
        {
            if (candidate is null)
            {
                continue;
            }

            var category = Normalize(candidate);
            if (byId.TryAdd(category.Id, category))
            {
                normalized.Add(category);
            }
        }

        for (var index = 0; index < normalized.Count; index++)
        {
            var category = normalized[index];
            var (parentId, _) = ResolveParent(category, byId);
            if (parentId != category.ParentId)
            {
                normalized[index] = category with { ParentId = parentId };
            }
        }

        DeduplicateSiblingNames(normalized);
        return normalized;
    }

    /// <summary>
    /// Renames the later siblings of a duplicate name (case-insensitive) so that
    /// every parent owns uniquely named children. Empty names are left alone.
    /// </summary>
    private static void DeduplicateSiblingNames(List<Category> categories)
    {
        var usedNames = new Dictionary<Guid, HashSet<string>>();
        for (var index = 0; index < categories.Count; index++)
        {
            var category = categories[index];
            if (category.Name.Length == 0)
            {
                continue;
            }

            var parentKey = category.ParentId ?? Guid.Empty;
            if (!usedNames.TryGetValue(parentKey, out var names))
            {
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                usedNames[parentKey] = names;
            }

            var uniqueName = category.Name;
            for (var suffix = 2; !names.Add(uniqueName); suffix++)
            {
                uniqueName = $"{category.Name} ({suffix})";
            }

            if (!string.Equals(uniqueName, category.Name, StringComparison.Ordinal))
            {
                categories[index] = category with { Name = uniqueName };
            }
        }
    }

    private static (Guid? ParentId, int Depth) ResolveParent(
        Category category,
        IReadOnlyDictionary<Guid, Category> byId)
    {
        if (category.ParentId is not { } parentId ||
            parentId == category.Id ||
            !byId.ContainsKey(parentId))
        {
            return (null, 1);
        }

        var chain = new List<Guid>();
        var seen = new HashSet<Guid> { category.Id };
        Guid? current = parentId;
        while (current is { } id && byId.TryGetValue(id, out var ancestor))
        {
            if (!seen.Add(id))
            {
                // A cycle: keep the category but detach it from the loop.
                return (null, 1);
            }

            chain.Add(id);
            current = ancestor.ParentId is { } next && next != id && byId.ContainsKey(next) ? next : null;
        }

        var depth = chain.Count + 1;
        if (depth <= MaxCategoryDepth)
        {
            return (chain[0], depth);
        }

        // chain[0] is the direct parent, chain[depth - MaxCategoryDepth] is the
        // ancestor that leaves the category at exactly the deepest allowed level.
        return (chain[depth - MaxCategoryDepth], MaxCategoryDepth);
    }
}
