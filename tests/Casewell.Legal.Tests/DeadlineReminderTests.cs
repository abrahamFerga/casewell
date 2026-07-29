using Cortex.Modules.Legal.Persistence;

namespace Cortex.Modules.Legal.Tests;

/// <summary>
/// The two-stage reminder rule: a lead-time nudge inside the window, an overdue notice after the
/// date, each at most once and in order — and an event discovered already overdue gets only the
/// overdue notice, never a stale "due soon".
/// </summary>
public sealed class DeadlineReminderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);
    private const int Lead = 3;

    [Fact]
    public void OutsideTheLeadWindow_NothingFires()
    {
        Assert.Null(MatterEvent.ReminderStageDue(Now, Now.AddDays(Lead).AddMinutes(1), currentStage: 0, Lead));
    }

    [Fact]
    public void InsideTheLeadWindow_TheNudgeFiresOnce()
    {
        Assert.Equal(1, MatterEvent.ReminderStageDue(Now, Now.AddDays(2), currentStage: 0, Lead));
        Assert.Null(MatterEvent.ReminderStageDue(Now, Now.AddDays(2), currentStage: 1, Lead));
    }

    [Fact]
    public void OnceOverdue_TheNoticeFiresOnce_EvenAfterTheNudge()
    {
        Assert.Equal(2, MatterEvent.ReminderStageDue(Now, Now.AddHours(-1), currentStage: 1, Lead));
        Assert.Null(MatterEvent.ReminderStageDue(Now, Now.AddHours(-1), currentStage: 2, Lead));
    }

    [Fact]
    public void AnEventDiscoveredAlreadyOverdue_SkipsStraightToTheOverdueNotice()
    {
        Assert.Equal(2, MatterEvent.ReminderStageDue(Now, Now.AddDays(-5), currentStage: 0, Lead));
    }
}
