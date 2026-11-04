using AegisVault.Core.Models;
using AegisVault.Core.Services;
using Xunit;

namespace AegisVault.Core.Tests.Services;

public sealed class VaultCsvExporterTests
{
    [Fact]
    public void RoundTripsEntriesThroughTheImporter()
    {
        var category = new Category { Name = "Work" };
        var entries = new List<PasswordEntry>
        {
            new()
            {
                Title = "GitHub, \"work\"",
                Username = "octocat",
                Password = "s3cret\r\nline",
                Url = "https://github.com",
                Urls = ["https://github.com", "https://gist.github.com"],
                Notes = "first\nsecond",
                TotpSecret = "JBSWY3DPEHPK3PXP",
                Phone = "13800000000",
                Email = "ops@example.com",
                AppId = "cli_app",
                Secret = "client-secret",
                ApiKey = "api-key",
                IsFavorite = true,
                CategoryId = category.Id,
            },
            // Recycled entries never leave the application.
            new() { Title = "Recycled", DeletedAt = DateTimeOffset.UtcNow },
        };

        var csv = VaultCsvExporter.Export(entries, [category]);
        var (parsed, skipped) = VaultCsvImporter.Parse(csv);

        Assert.Equal(0, skipped);
        var entry = Assert.Single(parsed);
        Assert.Equal("GitHub, \"work\"", entry.Title);
        Assert.Equal("octocat", entry.Username);
        Assert.Equal("s3cret\r\nline", entry.Password);
        Assert.Equal("first\nsecond", entry.Notes);
        Assert.Equal(["https://github.com", "https://gist.github.com"], entry.Urls);
        Assert.Equal("https://github.com", entry.Url);
        Assert.Equal("13800000000", entry.Phone);
        Assert.Equal("ops@example.com", entry.Email);
        Assert.Equal("cli_app", entry.AppId);
        Assert.Equal("client-secret", entry.Secret);
        Assert.Equal("api-key", entry.ApiKey);
        Assert.Equal("JBSWY3DPEHPK3PXP", entry.TotpSecret);
        Assert.True(entry.IsFavorite);

        // The Bitwarden layout has no category column, so the folder round-trips as a tag.
        Assert.Equal(["Work"], entry.Tags);
    }

    [Fact]
    public void EmptyVaultProducesOnlyTheHeader()
    {
        var csv = VaultCsvExporter.Export([], []);
        var rows = VaultCsvImporter.ParseCsv(csv);

        var header = Assert.Single(rows);
        Assert.Equal("folder", header[0]);
        Assert.Contains("login_password", header);
        Assert.Contains("api_key", header);
    }

    [Fact]
    public void PlainValuesAreNotQuoted()
    {
        var csv = VaultCsvExporter.Export(
            [new PasswordEntry { Title = "Simple", Username = "u", Password = "p" }],
            []);

        var rows = VaultCsvImporter.ParseCsv(csv);
        Assert.Equal(2, rows.Count);
        Assert.Contains(",Simple,", csv);
    }

    /// <summary>
    /// CWE-1236. A spreadsheet evaluates a cell that starts with one of these
    /// characters, so an entry that arrived from an untrusted CSV must not be able
    /// to smuggle a formula into a later plain-text export.
    /// </summary>
    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM(A1)")]
    [InlineData("=WEBSERVICE(\"http://evil\")")]
    [InlineData("\tcmd")]
    [InlineData("  =1+1")]
    public void FormulaPayloadsAreNeutralisedOnExport(string payload)
    {
        var csv = VaultCsvExporter.Export(
            [new PasswordEntry { Title = payload, Username = "u", Password = "p" }],
            []);

        var rows = VaultCsvImporter.ParseCsv(csv);
        var titleIndex = Array.IndexOf(VaultCsvExporter.Headers, "name");
        var title = rows[1][titleIndex];

        // ParseCsv already removed the RFC-4180 wrapper, so the cell starts with the
        // guard and ends with the payload.
        Assert.StartsWith("'", title, StringComparison.Ordinal);
        Assert.EndsWith(payload, title, StringComparison.Ordinal);

        // And the importer returns exactly the original value.
        Assert.Equal(payload, VaultCsvExporter.StripFormulaEscape(title));
    }

    [Fact]
    public void FormulaEscapingDoesNotAffectOrdinaryValues()
    {
        var csv = VaultCsvExporter.Export(
            [new PasswordEntry { Title = "GitHub", Username = "-u", Password = "p" }],
            []);

        var rows = VaultCsvImporter.ParseCsv(csv);
        var titleIndex = Array.IndexOf(VaultCsvExporter.Headers, "name");
        var usernameIndex = Array.IndexOf(VaultCsvExporter.Headers, "login_username");

        Assert.Equal("GitHub", rows[1][titleIndex]);
        // "-u" is a payload, so the username column gets the guard.
        Assert.Equal("'-u", rows[1][usernameIndex]);
    }

    [Fact]
    public void FormulaPayloadSurvivesAnExportImportRoundTrip()
    {
        // The guard must not corrupt the stored value; only the on-disk form is escaped.
        var original = "=1+1";
        var csv = VaultCsvExporter.Export(
            [new PasswordEntry { Title = original, Username = "u", Password = "p" }],
            []);

        var (parsed, skipped) = VaultCsvImporter.Parse(csv);

        Assert.Equal(0, skipped);
        var entry = Assert.Single(parsed);
        Assert.Equal(original, entry.Title);
    }

    /// <summary>
    /// Values that already begin with an apostrophe are the hard case: the importer
    /// cannot tell the guard from the user's own character. A single apostrophe guard
    /// lost one character per round trip (<c>'=1+1</c> came back as <c>=1+1</c>), so the
    /// guard is now "leading apostrophes + 1" and the importer strips exactly one.
    /// </summary>
    [Theory]
    [InlineData("'=1+1")]
    [InlineData("''=1+1")]
    [InlineData("'''=SUM(A1)")]
    [InlineData("'@user")]
    [InlineData("'-1")]
    [InlineData("'=WEBSERVICE(\"x\")")]
    public void ValuesStartingWithAnApostropheRoundTripExactly(string original)
    {
        var csv = VaultCsvExporter.Export(
            [new PasswordEntry { Title = original, Username = "u", Password = "p" }],
            []);

        var (parsed, _) = VaultCsvImporter.Parse(csv);

        var entry = Assert.Single(parsed);
        Assert.Equal(original, entry.Title);
    }

    /// <summary>
    /// An apostrophe that does not precede a formula character is ordinary text and must
    /// not be touched in either direction.
    /// </summary>
    [Theory]
    [InlineData("'hello")]
    [InlineData("it's fine")]
    [InlineData("'")]
    public void OrdinaryApostrophesAreLeftAlone(string original)
    {
        var csv = VaultCsvExporter.Export(
            [new PasswordEntry { Title = original, Username = "u", Password = "p" }],
            []);

        var (parsed, _) = VaultCsvImporter.Parse(csv);

        var entry = Assert.Single(parsed);
        Assert.Equal(original, entry.Title);
    }

    /// <summary>
    /// A whole sweep of values that look like formulas or passwords must survive the
    /// round trip, so the guard can never silently rewrite user data.
    /// <para>
    /// Leading whitespace is excluded on purpose: the importer trims every field (that
    /// is what makes third-party CSVs usable), so <c>"  =1+1"</c> and <c>"\tcmd"</c>
    /// lose their prefix regardless of the formula guard. That is a pre-existing
    /// limitation of the importer, not something the guard introduced.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("=1+1")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM(A1)")]
    [InlineData("-my-password")]
    [InlineData("@user")]
    [InlineData("+1234567")]
    [InlineData("GitHub")]
    public void EveryPayloadSurvivesTheRoundTrip(string original)
    {
        var csv = VaultCsvExporter.Export(
            [new PasswordEntry { Title = original, Username = "u", Password = "p" }],
            []);

        var (parsed, _) = VaultCsvImporter.Parse(csv);

        var entry = Assert.Single(parsed);
        Assert.Equal(original, entry.Title);
    }
}
