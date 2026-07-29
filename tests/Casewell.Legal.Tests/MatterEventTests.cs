using Cortex.Modules.Legal.Persistence;

namespace Cortex.Modules.Legal.Tests;

public sealed class MatterEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UrgencyAt_ClassifiesOverdueDueSoonAndUpcoming()
    {
        Assert.Equal(EventUrgency.Overdue, MatterEvent.UrgencyAt(Now, Now.AddMinutes(-1)));
        Assert.Equal(EventUrgency.DueSoon, MatterEvent.UrgencyAt(Now, Now.AddDays(1)));
        Assert.Equal(EventUrgency.DueSoon, MatterEvent.UrgencyAt(Now, Now.AddDays(3)));
        Assert.Equal(EventUrgency.Upcoming, MatterEvent.UrgencyAt(Now, Now.AddDays(3).AddMinutes(1)));
    }

    [Fact]
    public void UrgencyAt_HonorsACustomDueSoonWindow()
    {
        Assert.Equal(EventUrgency.DueSoon, MatterEvent.UrgencyAt(Now, Now.AddDays(6), dueSoonDays: 7));
        Assert.Equal(EventUrgency.Upcoming, MatterEvent.UrgencyAt(Now, Now.AddDays(6)));
    }

    [Theory]
    [InlineData("deadline", "deadline")]
    [InlineData("Filing", "deadline")]
    [InlineData("COURT", "hearing")]
    [InlineData("trial", "hearing")]
    [InlineData("call", "meeting")]
    [InlineData("whatever", "reminder")]
    [InlineData(null, "reminder")]
    public void NormalizeType_MapsSynonymsAndDefaultsToReminder(string? input, string expected)
    {
        Assert.Equal(expected, MatterEvent.NormalizeType(input));
    }
}
