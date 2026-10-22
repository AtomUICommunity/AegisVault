namespace AegisVault.Core.Models;

/// <summary>
/// Decrypted password entry model. Lives only in memory while a vault is unlocked.
/// </summary>
public sealed record PasswordEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Title { get; init; } = string.Empty;

    public string Username { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    /// <summary>All web addresses for this entry; <see cref="Url"/> mirrors the first one.</summary>
    public List<string> Urls { get; init; } = [];

    public string Notes { get; init; } = string.Empty;

    public string TotpSecret { get; init; } = string.Empty;

    /// <summary>Optional contact phone number (payload v3, shown in clear).</summary>
    public string Phone { get; init; } = string.Empty;

    /// <summary>Optional e-mail address (payload v4, shown in clear).</summary>
    public string Email { get; init; } = string.Empty;

    /// <summary>Optional application id / client id (payload v3, shown in clear).</summary>
    public string AppId { get; init; } = string.Empty;

    /// <summary>Optional client secret (payload v3, masked by the UI).</summary>
    public string Secret { get; init; } = string.Empty;

    /// <summary>Optional API key (payload v3, masked by the UI).</summary>
    public string ApiKey { get; init; } = string.Empty;

    public List<string> Tags { get; init; } = [];

    /// <summary>Single-assignment category; null means "uncategorized".</summary>
    public Guid? CategoryId { get; init; }

    public Dictionary<string, string> CustomFields { get; init; } = [];

    public bool IsFavorite { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the entry was moved to the recycle bin (payload v5); <c>null</c> means
    /// it is live. Entries with a value are excluded from the live view and are
    /// purged automatically once they age past the retention window.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; init; }

    /// <summary>
    /// When the entry was last viewed (payload v5); <c>null</c> means never.
    /// Written by <c>VaultService.MarkEntryOpened</c>, which deliberately does
    /// <b>not</b> touch <see cref="UpdatedAt"/> so the "stale" health view and the
    /// "recently updated" sort stay meaningful.
    /// </summary>
    public DateTimeOffset? LastOpenedAt { get; init; }

    /// <summary>
    /// Superseded passwords, newest first, capped at
    /// <see cref="MaximumPasswordHistory"/> (payload v6). Encrypted like the rest
    /// of the entry, and only appended when the password itself changes.
    /// </summary>
    public IReadOnlyList<PasswordHistoryItem>? PasswordHistory { get; init; }

    /// <summary>How many old passwords are retained per entry.</summary>
    public const int MaximumPasswordHistory = 10;

    /// <summary>
    /// Redacts secrets: the compiler-generated record dump (which the UI automation
    /// tree picks up as the list item name) would otherwise expose the password.
    /// </summary>
    public override string ToString() => $"PasswordEntry({Title})";
}
