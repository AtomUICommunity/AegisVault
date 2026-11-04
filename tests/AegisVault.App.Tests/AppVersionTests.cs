using System.Reflection;
using AegisVault.App;
using Xunit;
using Xunit.Abstractions;

namespace AegisVault.App.Tests;

/// <summary>
/// The two version strings the UI shows must come from one place and must not drift.
/// The unlock page shows the short form (a build hash is noise when you are deciding
/// whether to unlock); the settings page shows the full form (a bug report needs to be
/// tied to a build).
/// </summary>
public sealed class AppVersionTests
{
    private readonly ITestOutputHelper _out;

    public AppVersionTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void ShortIsThreeNumericParts()
    {
        var shortForm = AppVersion.Short;
        _out.WriteLine($"Short = {shortForm}");

        var parts = shortForm.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.All(parts, part => Assert.True(int.TryParse(part, out _), $"'{part}' is not numeric"));
    }

    [Fact]
    public void FullStartsWithTheShortForm()
    {
        _out.WriteLine($"Short = {AppVersion.Short}, Full = {AppVersion.Full}");

        // A regression here would mean the two labels describe different builds.
        Assert.StartsWith(AppVersion.Short, AppVersion.Full, StringComparison.Ordinal);
    }

    [Fact]
    public void FullCarriesTheSourceRevision()
    {
        _out.WriteLine($"Full = {AppVersion.Full}");

        // The SDK appends "+<sha>" from SourceRevisionId; a build without it would make
        // bug reports ambiguous, so assert the shape rather than the exact hash.
        var plus = AppVersion.Full.IndexOf('+');
        Assert.True(plus > 0, $"Expected a '+revision' suffix, got '{AppVersion.Full}'");

        var revision = AppVersion.Full[(plus + 1)..];
        Assert.NotEmpty(revision);
        Assert.DoesNotContain('+', revision);
    }

    [Fact]
    public void ShortHasNoBuildMetadata()
    {
        // The unlock page is deliberately kept free of the hash.
        Assert.DoesNotContain('+', AppVersion.Short);
    }

    /// <summary>
    /// The parsing rule itself, independent of the running build: a second '+' and
    /// anything after it is dropped so the label stays compact.
    /// </summary>
    [Theory]
    [InlineData("0.6.3+abc123", "0.6.3", "0.6.3+abc123")]
    [InlineData("0.6.3+abc123.20261104", "0.6.3", "0.6.3+abc123.20261104")]
    [InlineData("0.6.3+abc+extra", "0.6.3", "0.6.3+abc")]
    [InlineData("0.6.3", "0.6.3", "0.6.3")]
    [InlineData("", "0.6.3", "0.6.3")]
    public void InformationalVersionIsReducedToVersionPlusOneRevision(
        string informational,
        string expectedShort,
        string expectedFull)
    {
        var (shortForm, full) = AppVersion.Parse(informational, "0.6.3");
        _out.WriteLine($"'{informational}' -> short='{shortForm}' full='{full}'");

        Assert.Equal(expectedShort, shortForm);
        Assert.Equal(expectedFull, full);
    }
}
