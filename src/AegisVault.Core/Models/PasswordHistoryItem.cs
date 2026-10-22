namespace AegisVault.Core.Models;

/// <summary>
/// One superseded password, kept so a rejected change can be traced back (and so
/// an old value can still be read/copied). Lives in the encrypted entry payload.
/// </summary>
public sealed record PasswordHistoryItem
{
    public string Password { get; init; } = string.Empty;

    /// <summary>When this value was replaced, in UTC.</summary>
    public DateTimeOffset ChangedAt { get; init; }
}
