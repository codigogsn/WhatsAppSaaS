using FluentAssertions;
using WhatsAppSaaS.Application.Services;

namespace Api.Tests.Services;

/// <summary>
/// Tests that customer names are normalized consistently across
/// greeting, persistence, and dashboard display paths.
/// </summary>
public sealed class CustomerNameNormalizationTests
{
    // ═══════════════════════════════════════════════════════════
    //  1. Greeting with raw DB name "* GONZALO" displays "Gonzalo"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Greeting_RawDbName_DisplaysNormalized()
    {
        var greeting = Msg.ReturningGreeting("Mi Restaurant",
            WebhookProcessor.NormalizeDisplayName("* GONZALO"));

        greeting.Should().Contain("Gonzalo");
        greeting.Should().NotContain("* GONZALO");
        greeting.Should().NotContain("GONZALO");
    }

    // ═══════════════════════════════════════════════════════════
    //  2. New customer "gonzalo" is normalized to "Gonzalo"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void NewCustomer_LowercaseName_PersistedAsTitleCase()
    {
        var result = WebhookProcessor.NormalizeDisplayName("gonzalo");
        result.Should().Be("Gonzalo");
    }

    // ═══════════════════════════════════════════════════════════
    //  3. New customer "* GONZALO" is normalized to "Gonzalo"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void NewCustomer_AsteriskAllCaps_PersistedAsTitleCase()
    {
        var result = WebhookProcessor.NormalizeDisplayName("* GONZALO");
        result.Should().Be("Gonzalo");
    }

    // ═══════════════════════════════════════════════════════════
    //  4. Multi-word name normalized correctly
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("maria fernanda", "Maria Fernanda")]
    [InlineData("MARIA FERNANDA", "Maria Fernanda")]
    [InlineData("* MARIA FERNANDA *", "Maria Fernanda")]
    [InlineData("  maria   fernanda  ", "Maria Fernanda")]
    public void MultiWordName_NormalizedToTitleCase(string input, string expected)
    {
        WebhookProcessor.NormalizeDisplayName(input).Should().Be(expected);
    }

    // ═══════════════════════════════════════════════════════════
    //  5. Existing stored customer "* GONZALO" is normalized by backfill
    //     (tested via the normalization function that backfill uses)
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("* GONZALO", "Gonzalo")]
    [InlineData("*  MARIA  JOSE *", "Maria Jose")]
    [InlineData("  * jean   carlos * ", "Jean Carlos")]
    [InlineData("PEDRO", "Pedro")]
    public void BackfillPath_NormalizesExistingBadNames(string stored, string expected)
    {
        // The backfill endpoint uses the same NormalizeDisplayName function
        var normalized = WebhookProcessor.NormalizeDisplayName(stored);
        normalized.Should().Be(expected);
        // Verify it's different from input (would trigger an update)
        normalized.Should().NotBe(stored);
    }

    // ═══════════════════════════════════════════════════════════
    //  6. Dashboard customer DTO returns normalized name
    //     (tested via the normalization function applied at read)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void DashboardReadPath_NormalizesRawDbValue()
    {
        // Simulates what AdminCustomersController now does when reading from DB
        string? rawName = "* GONZALO";
        var displayName = !string.IsNullOrWhiteSpace(rawName)
            ? WebhookProcessor.NormalizeDisplayName(rawName)
            : "N/A";
        displayName.Should().Be("Gonzalo");
    }

    [Fact]
    public void DashboardReadPath_NullName_FallsBackToNA()
    {
        string? rawName = null;
        var displayName = !string.IsNullOrWhiteSpace(rawName)
            ? WebhookProcessor.NormalizeDisplayName(rawName)
            : "N/A";
        displayName.Should().Be("N/A");
    }

    // ═══════════════════════════════════════════════════════════
    //  Edge cases — safe handling
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void NormalizeDisplayName_EmptyString_ReturnsSafely()
    {
        WebhookProcessor.NormalizeDisplayName("").Should().Be("");
    }

    [Fact]
    public void NormalizeDisplayName_AlreadyNormalized_Unchanged()
    {
        WebhookProcessor.NormalizeDisplayName("Gonzalo").Should().Be("Gonzalo");
        WebhookProcessor.NormalizeDisplayName("Maria Fernanda").Should().Be("Maria Fernanda");
    }

    [Fact]
    public void NormalizeDisplayName_SingleChar_UpperCased()
    {
        WebhookProcessor.NormalizeDisplayName("j").Should().Be("J");
    }

    [Theory]
    [InlineData("*carlos*", "Carlos")]
    [InlineData("#ADMIN#", "Admin")]
    [InlineData("~test~", "Test")]
    [InlineData("_underscored_", "Underscored")]
    public void NormalizeDisplayName_StripsAllDecorativeChars(string input, string expected)
    {
        WebhookProcessor.NormalizeDisplayName(input).Should().Be(expected);
    }

    // ═══════════════════════════════════════════════════════════
    //  Consistency: same function used everywhere
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void AllPaths_UseSameNormalization_ConsistentResult()
    {
        var rawName = "* GONZALO";

        // Greeting path
        var greetingName = WebhookProcessor.NormalizeDisplayName(rawName);
        // Persistence path (CustomerService now normalizes via same function)
        var persistedName = WebhookProcessor.NormalizeDisplayName(rawName);
        // Dashboard read path
        var dashboardName = WebhookProcessor.NormalizeDisplayName(rawName);

        greetingName.Should().Be("Gonzalo");
        persistedName.Should().Be("Gonzalo");
        dashboardName.Should().Be("Gonzalo");

        // All three must be identical
        greetingName.Should().Be(persistedName).And.Be(dashboardName);
    }
}
