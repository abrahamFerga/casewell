using Cortex.Modules.Legal;
using Cortex.Modules.Legal.Persistence;

namespace Cortex.Modules.Legal.Tests;

/// <summary>
/// The thumbs → 1–5 mapping behind SPEC's "AI helpfulness &gt;= 4.0/5". Pure arithmetic, and the
/// one part of the feature that can be wrong while every request still returns 200.
/// </summary>
public sealed class AssistantHelpfulnessTests
{
    [Fact]
    public void Score_IsNull_WhenNothingHasBeenRated()
    {
        // An unrated assistant has no score. Returning 0 (or a neutral 3) would let "nobody
        // answered" read as a measurement, and the metric would look met before it was measured.
        Assert.Null(AssistantHelpfulness.Score(0, 0));
    }

    [Theory]
    [InlineData(1, 0, 5.0)]   // all up
    [InlineData(0, 1, 1.0)]   // all down
    [InlineData(1, 1, 3.0)]   // even split
    [InlineData(4, 1, 4.2)]   // clears the >= 4.0 bar
    [InlineData(3, 1, 4.0)]   // exactly at the bar
    [InlineData(7, 3, 3.8)]   // below the bar
    public void Score_MapsTheHelpfulRatioOntoOneThroughFive(int helpful, int unhelpful, double expected)
    {
        Assert.Equal(expected, AssistantHelpfulness.Score(helpful, unhelpful)!.Value, 3);
    }

    [Fact]
    public void Score_NeverLeavesTheOneToFiveRange()
    {
        foreach (var (up, down) in new[] { (0, 99), (99, 0), (50, 50), (1, 998) })
        {
            var score = AssistantHelpfulness.Score(up, down)!.Value;
            Assert.InRange(score, 1.0, 5.0);
        }
    }
}

/// <summary>
/// The ethical wall — the mechanism behind "chat scoped to a matter answers from matter data only".
/// It shipped with the matter epic and had no test of its own until this one; a wall that silently
/// stops restricting is invisible, because every request still succeeds.
/// </summary>
public sealed class MatterWallTests
{
    private static readonly Guid Insider = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Outsider = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void NoWall_IsOpenToTheWholeTenant()
    {
        Assert.True(Matter.WallAllows(null, Outsider));
    }

    [Fact]
    public void AWall_AdmitsOnlyTheListedUsers()
    {
        var wall = $"[\"{Insider}\"]";

        Assert.True(Matter.WallAllows(wall, Insider));
        Assert.False(Matter.WallAllows(wall, Outsider));
    }

    [Fact]
    public void AWall_FailsClosed_WithoutAnIdentity()
    {
        // No identity is not "no restriction": an unauthenticated caller must not read a walled
        // matter just because there is nobody to compare against.
        Assert.False(Matter.WallAllows($"[\"{Insider}\"]", null));
    }

    [Fact]
    public void ACorruptWall_FailsClosed_RatherThanOpeningTheMatter()
    {
        Assert.False(Matter.WallAllows("{not json", Insider));
        Assert.False(Matter.WallAllows("{not json", Outsider));
    }

    [Fact]
    public void IsAccessibleTo_UsesTheWall()
    {
        var walled = new Matter { TenantId = Guid.NewGuid(), Name = "Walled", RestrictedUserIdsJson = $"[\"{Insider}\"]" };
        var open = new Matter { TenantId = Guid.NewGuid(), Name = "Open" };

        Assert.True(walled.IsAccessibleTo(Insider));
        Assert.False(walled.IsAccessibleTo(Outsider));
        Assert.True(open.IsAccessibleTo(Outsider));
    }
}

/// <summary>
/// ABA 5.3: client-facing output the assistant produced must announce that it is AI-drafted, so
/// the reviewing attorney knows which kind of scrutiny the document needs.
/// </summary>
public sealed class AiDraftedBannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheDraftBanner_SaysItIsAiDrafted_NotMerelyThatItIsADraft()
    {
        Assert.Contains("AI-DRAFTED", BriefingTools.DraftBanner, StringComparison.Ordinal);
        Assert.Contains("attorney must review", BriefingTools.DraftBanner, StringComparison.OrdinalIgnoreCase);
    }

    // The one above watches the constant's WORDING; these watch its DELIVERY. Deleting the append
    // in StatusLetter.Compose leaves the wording test green and ships undisclosed client letters,
    // so a test over the composed letter is the only thing standing behind #22's banner criterion.
    [Fact]
    public void EveryComposedLetter_CarriesTheDisclosure_EvenWhenTheMatterIsEmpty()
    {
        var letter = StatusLetter.Compose("Meridian acquisition", "Meridian Ltd", Now, [], [], 0m);

        Assert.Contains(BriefingTools.DraftBanner, letter, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDisclosure_IsTheLastThingTheReaderSees()
    {
        var letter = StatusLetter.Compose(
            "Meridian acquisition", "Meridian Ltd", Now,
            [new LetterMilestone("Filed the response", Now.AddDays(-3))],
            [new LetterMilestone("Hearing", Now.AddDays(14))],
            12.5m);

        Assert.EndsWith(BriefingTools.DraftBanner, letter.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void StrippingTheBanner_IsWhatTurnsADraftIntoTheClientCopy()
    {
        // SendStatusUpdate removes the banner because the approval that released the send IS the
        // review record. That strip must take the disclosure and nothing else with it.
        var letter = StatusLetter.Compose("Meridian acquisition", "Meridian Ltd", Now, [], [], 4m);

        var clientCopy = letter.Replace(BriefingTools.DraftBanner, "", StringComparison.Ordinal);

        Assert.DoesNotContain("AI-DRAFTED", clientCopy, StringComparison.Ordinal);
        Assert.Contains("Dear Meridian Ltd,", clientCopy, StringComparison.Ordinal);
        Assert.Contains("Time devoted to your matter in the last 30 days: 4 hours.", clientCopy, StringComparison.Ordinal);
    }
}
