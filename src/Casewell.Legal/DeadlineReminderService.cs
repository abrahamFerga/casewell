using Cortex.Application.Notifications;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cortex.Modules.Legal;

/// <summary>
/// The two-stage deadline reminder the README promises: a lead-time nudge (3 days out) and an
/// overdue notice, delivered through the platform notifier — in-app always, plus whatever
/// channels the host registered (email, webhooks). Sweeps run tenant-blind (IgnoreQueryFilters)
/// because there is no ambient tenant in the background; every notification carries its explicit
/// tenant + recipient, which is exactly the notifier's contract. Completed events, closed
/// matters, and pre-reminder-era events (no recorded creator) never notify.
/// </summary>
public sealed class DeadlineReminderService(
    IServiceScopeFactory scopes,
    ILogger<DeadlineReminderService> logger) : BackgroundService
{
    /// <summary>Days before the due date the first-stage nudge fires.</summary>
    public const int LeadDays = 3;

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            // First tick after one full interval: startup work first, and tests can drive
            // SweepOnceAsync deterministically without racing the service.
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    await SweepOnceAsync(scope.ServiceProvider, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Deadline reminder sweep failed; retrying on the next tick.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }

    /// <summary>One sweep: notify every open event whose next reminder stage is due, then advance
    /// its stage — at most one lead nudge and one overdue notice per event, ever.</summary>
    public static async Task<int> SweepOnceAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<LegalDbContext>();
        var notifier = services.GetRequiredService<INotifier>();
        var now = DateTimeOffset.UtcNow;

        // Tenant-blind candidate fetch; only open matters keep reminding.
        var candidates = await db.MatterEvents.IgnoreQueryFilters()
            .Where(e => e.CompletedAt == null && e.CreatedByUserId != null &&
                        e.ReminderStage < 2 && e.StartsAt <= now.AddDays(LeadDays))
            .Join(db.Matters.IgnoreQueryFilters().Where(m => m.Status == MatterStatus.Open),
                e => e.MatterId, m => m.Id, (e, m) => new { Event = e, MatterName = m.Name })
            .OrderBy(x => x.Event.StartsAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var candidate in candidates)
        {
            var e = candidate.Event;
            var stage = MatterEvent.ReminderStageDue(now, e.StartsAt, e.ReminderStage, LeadDays);
            if (stage is null)
            {
                continue;
            }

            var (title, body) = stage == 2
                ? ($"OVERDUE: {e.Title}",
                   $"'{e.Title}' on matter '{candidate.MatterName}' was due {e.StartsAt:yyyy-MM-dd HH:mm}Z and is not marked completed. " +
                   "Complete it (complete_event) or reschedule it.")
                : ($"Due soon: {e.Title}",
                   $"'{e.Title}' on matter '{candidate.MatterName}' is due {e.StartsAt:yyyy-MM-dd HH:mm}Z.");

            await notifier.NotifyAsync(new Notification(
                e.TenantId, e.CreatedByUserId!.Value, "legal.deadlines", title, body,
                Link: "/legal/calendar"), cancellationToken);

            e.ReminderStage = stage.Value;
            sent++;
        }

        if (sent > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return sent;
    }
}
