using Cortex.Modules.Legal;
using Cortex.Modules.Legal.Persistence;

namespace Cortex.Modules.Legal.Tests;

public sealed class MatterNumberingTests
{
    [Fact]
    public void Format_PadsSequenceToFourDigits()
    {
        Assert.Equal("2026-0007", MatterNumbering.Format(2026, 7));
        Assert.Equal("2026-10001", MatterNumbering.Format(2026, 10001)); // no truncation past 9999
    }

    [Fact]
    public void NextSequence_StartsAtOne_AndCountsOnlyTheGivenYear()
    {
        Assert.Equal(1, MatterNumbering.NextSequence([], 2026));
        Assert.Equal(3, MatterNumbering.NextSequence(["2026-0001", "2026-0002", "2025-0099"], 2026));
        Assert.Equal(1, MatterNumbering.NextSequence(["2025-0099"], 2026));
    }

    [Fact]
    public void NextSequence_IgnoresMalformedAndNullNumbers()
    {
        Assert.Equal(6, MatterNumbering.NextSequence(
            [null, "garbage", "2026-", "2026-abc", "2026-0005"], 2026));
    }
}

public sealed class PracticeAreaTests
{
    [Theory]
    [InlineData("litigation", "Litigation")]
    [InlineData("IP", "Intellectual Property")]
    [InlineData("employment law", "Employment")]
    [InlineData("m&a", "Corporate")]
    [InlineData("  Tax  ", "Tax")]
    public void Normalize_MapsForgivingInputToCanonicalArea(string input, string expected)
    {
        Assert.Equal(expected, PracticeAreas.Normalize(input));
    }

    [Fact]
    public void Normalize_UnknownOrBlank_ReturnsNull()
    {
        Assert.Null(PracticeAreas.Normalize("maritime salvage"));
        Assert.Null(PracticeAreas.Normalize("   "));
        Assert.Null(PracticeAreas.Normalize(null));
    }
}

public sealed class MatterLifecycleTests
{
    private static Matter NewMatter() => new() { TenantId = Guid.NewGuid(), Name = "Test matter" };

    [Fact]
    public void Close_SetsClosedAt_AndReopenClearsIt()
    {
        var matter = NewMatter();
        var now = DateTimeOffset.UtcNow;

        matter.ApplyStatus(MatterStatus.Closed, now);
        Assert.Equal(MatterStatus.Closed, matter.Status);
        Assert.Equal(now, matter.ClosedAt);

        matter.ApplyStatus(MatterStatus.Open, now.AddDays(1));
        Assert.Equal(MatterStatus.Open, matter.Status);
        Assert.Null(matter.ClosedAt);
    }

    [Fact]
    public void ReClosing_KeepsTheOriginalCloseDate()
    {
        var matter = NewMatter();
        var first = DateTimeOffset.UtcNow;

        matter.ApplyStatus(MatterStatus.Closed, first);
        matter.ApplyStatus(MatterStatus.Closed, first.AddDays(5));

        Assert.Equal(first, matter.ClosedAt);
    }

    [Fact]
    public void Hold_DoesNotCount_AsClosed()
    {
        var matter = NewMatter();

        var message = matter.ApplyStatus(MatterStatus.OnHold, DateTimeOffset.UtcNow);

        Assert.Equal(MatterStatus.OnHold, matter.Status);
        Assert.Null(matter.ClosedAt);
        Assert.Contains("on hold", message, StringComparison.Ordinal);
    }
}
