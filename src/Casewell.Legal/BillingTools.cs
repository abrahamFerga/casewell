using System.ComponentModel;
using System.Globalization;
using System.Text;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Modules.Legal;

/// <summary>
/// Billing and invoicing — time capture with a timer, recoverable expenses, and invoices assembled
/// from unbilled time + expenses + an optional flat fee.
/// <para>
/// Two rules carry the compliance weight. <b>Separation of duties:</b> the user who drafted an
/// invoice can never approve it, because an attorney approving their own bill is the control every
/// firm's engagement letter promises the client. <b>Bill once:</b> a draft stamps its InvoiceId on
/// every line's source row, so the same hour cannot reach a second invoice.
/// </para>
/// <para>
/// Timer writes are NOT approval-gated, matching <c>log_time</c>: capture friction is why lawyers
/// under-record time, and a timer is own-user and correctable. Everything that touches a client's
/// bill — expenses, drafts, approvals, sends — is gated.
/// </para>
/// </summary>
public sealed class BillingTools(
    LegalDbContext db,
    ITenantContext tenant,
    ICurrentUser currentUser)
{
    [Description("Start a running timer on a matter. Use when the user says they are starting work ('start a timer on Acme', 'I'm on the Acme call'). Not approval-gated. Only one timer runs at a time; starting a second is refused so the first is not silently lost.")]
    public async Task<string> StartTimer(
        [Description("The matter being worked on.")] string matterName,
        [Description("What is being worked on — becomes the bill's narrative line.")] string? description = null,
        [Description("UTBMS activity code such as L110 or A103. Omit and one is suggested from the description.")] string? activityCode = null,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var running = await CurrentTimerAsync(cancellationToken);
        if (running is not null)
        {
            var onMatter = await MatterNameAsync(running.MatterId, cancellationToken);
            return $"REFUSED: a timer is already running on '{onMatter}' since " +
                   $"{running.StartedAt:yyyy-MM-dd HH:mm} UTC " +
                   $"({Billing.ElapsedLabel(Billing.Elapsed(running.StartedAt))} so far). " +
                   "Stop it with stop_timer before starting another.";
        }

        var code = Billing.NormalizeActivityCode(activityCode) ?? Billing.SuggestActivityCode(description);

        db.RunningTimers.Add(new RunningTimer
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            UserId = currentUser.UserId,
            UserDisplay = currentUser.DisplayName,
            ActivityCode = code,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            StartedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);

        return $"Timer started on matter '{matter.Name}'" +
               $"{(code is null ? "" : $" [{code} {Billing.ActivityLabel(code)}]")}" +
               $"{(string.IsNullOrWhiteSpace(description) ? "" : $": {description.Trim()}")}. " +
               "Stop it with stop_timer and the elapsed time becomes a logged entry.";
    }

    [Description("Stop the running timer and log the elapsed time as a time entry on its matter. Not approval-gated. Elapsed time rounds up to the next tenth of an hour (six-minute increments), the standard legal billing unit.")]
    public async Task<string> StopTimer(
        [Description("Override or supply the narrative for the entry; omit to keep what start_timer recorded.")] string? description = null,
        [Description("Override the activity code.")] string? activityCode = null,
        [Description("Whether the time is billable (default true).")] bool billable = true,
        CancellationToken cancellationToken = default)
    {
        var running = await CurrentTimerAsync(cancellationToken);
        if (running is null)
        {
            return "No timer is running. Start one with start_timer, or log time directly with log_time.";
        }

        var matterName = await MatterNameAsync(running.MatterId, cancellationToken);
        var hours = Billing.ElapsedToHours(Billing.Elapsed(running.StartedAt));
        var narrative = !string.IsNullOrWhiteSpace(description)
            ? description.Trim()
            : running.Description ?? "Time recorded by timer";
        var code = Billing.NormalizeActivityCode(activityCode) ?? running.ActivityCode ?? Billing.SuggestActivityCode(narrative);

        db.TimeEntries.Add(new TimeEntry
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = running.MatterId,
            UserId = running.UserId,
            UserDisplay = running.UserDisplay,
            Hours = hours,
            Description = narrative,
            WorkedOn = DateOnly.FromDateTime(running.StartedAt.UtcDateTime),
            Billable = billable,
            ActivityCode = code,
        });
        db.RunningTimers.Remove(running);
        await db.SaveChangesAsync(cancellationToken);

        return $"Stopped the timer on matter '{matterName}': logged {hours:0.##}h" +
               $"{(code is null ? "" : $" [{code} {Billing.ActivityLabel(code)}]")}" +
               $"{(billable ? "" : " (non-billable)")} — {narrative}.";
    }

    [Description("Whether a timer is running, on which matter, and how long it has been going.")]
    public async Task<string> TimerStatus(CancellationToken cancellationToken = default)
    {
        var running = await CurrentTimerAsync(cancellationToken);
        if (running is null)
        {
            return "No timer is running.";
        }

        var matterName = await MatterNameAsync(running.MatterId, cancellationToken);
        var elapsed = Billing.Elapsed(running.StartedAt);
        return $"Timer running on matter '{matterName}' since {running.StartedAt:yyyy-MM-dd HH:mm} UTC — " +
               $"{Billing.ElapsedLabel(elapsed)} elapsed ({Billing.ElapsedToHours(elapsed):0.##}h if stopped now)" +
               $"{(running.ActivityCode is null ? "" : $" [{running.ActivityCode}]")}" +
               $"{(running.Description is null ? "" : $": {running.Description}")}.";
    }

    [Description("Record an expense the firm advanced on a matter and expects to recover (filing fee, courier, expert invoice, transcript). Side-effecting: it lands on a client's bill and requires human approval.")]
    public async Task<string> RecordExpense(
        [Description("The matter the expense belongs to.")] string matterName,
        [Description("Amount advanced, e.g. 402 or 87.50. Always positive.")] double amount,
        [Description("What it was for — the bill's line description.")] string description,
        [Description("The date incurred as an ISO date (default: today).")] string? date = null,
        [Description("Whether the expense is recoverable from the client (default true).")] bool billable = true,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        if (!Billing.AmountIsValid(amount))
        {
            return "amount must be greater than 0 (and under a billion — beyond that it's a typo).";
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return "An expense needs a description — it becomes the line the client reads.";
        }

        var incurredOn = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(date) && !DateOnly.TryParse(date, CultureInfo.InvariantCulture, out incurredOn))
        {
            return $"'{date}' is not a date I can parse — use an ISO date like 2026-07-15, or omit it for today.";
        }

        db.Expenses.Add(new Expense
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            UserId = currentUser.UserId,
            UserDisplay = currentUser.DisplayName,
            Amount = (decimal)amount,
            Description = description.Trim(),
            IncurredOn = incurredOn,
            Billable = billable,
        });
        await db.SaveChangesAsync(cancellationToken);

        return $"Recorded expense of {Billing.Money((decimal)amount)} on matter '{matter.Name}' " +
               $"for {incurredOn:yyyy-MM-dd}{(billable ? "" : " (non-recoverable)")}: {description.Trim()}.";
    }

    [Description("Draft an invoice for a matter from everything unbilled: billable time, recoverable expenses, and an optional flat fee. One step — no need to list anything first. The draft must be approved by SOMEONE ELSE before it can be sent. Side-effecting and requires human approval.")]
    public async Task<string> DraftInvoice(
        [Description("The matter to bill.")] string matterName,
        [Description("Hourly rate to bill time at, e.g. 350. Used for entries without their own agreed rate.")] double hourlyRate,
        [Description("Optional period start as an ISO date (inclusive); omit to bill from inception.")] string? fromDate = null,
        [Description("Optional period end as an ISO date (inclusive); omit for today.")] string? toDate = null,
        [Description("Optional flat fee to add as its own line, e.g. 2500 for a fixed-fee engagement.")] double? flatFee = null,
        [Description("What the flat fee covers; required when flatFee is given.")] string? flatFeeDescription = null,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        if (!Billing.RateIsValid(hourlyRate))
        {
            return "hourlyRate must be greater than 0 (and under 100,000 — beyond that it's a typo).";
        }

        if (flatFee is not null && !Billing.AmountIsValid(flatFee.Value))
        {
            return "flatFee must be greater than 0 when supplied.";
        }

        if (flatFee is not null && string.IsNullOrWhiteSpace(flatFeeDescription))
        {
            return "A flat fee needs a description saying what it covers — it is a line the client reads.";
        }

        DateOnly? from = null;
        if (!string.IsNullOrWhiteSpace(fromDate))
        {
            if (!DateOnly.TryParse(fromDate, CultureInfo.InvariantCulture, out var parsed))
            {
                return $"'{fromDate}' is not a date I can parse — use an ISO date like 2026-07-01, or omit it.";
            }

            from = parsed;
        }

        var to = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(toDate) && !DateOnly.TryParse(toDate, CultureInfo.InvariantCulture, out to))
        {
            return $"'{toDate}' is not a date I can parse — use an ISO date like 2026-07-31, or omit it for today.";
        }

        // Unbilled only: InvoiceId is the "already billed" stamp, so the same hour can never reach
        // a second invoice even if the period overlaps a previous draft.
        var time = await db.TimeEntries
            .Where(t => t.MatterId == matter.Id && t.InvoiceId == null && t.Billable
                        && (from == null || t.WorkedOn >= from) && t.WorkedOn <= to)
            .OrderBy(t => t.WorkedOn).ThenBy(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        var expenses = await db.Expenses
            .Where(e => e.MatterId == matter.Id && e.InvoiceId == null && e.Billable
                        && (from == null || e.IncurredOn >= from) && e.IncurredOn <= to)
            .OrderBy(e => e.IncurredOn).ThenBy(e => e.CreatedAt)
            .ToListAsync(cancellationToken);

        var draft = Billing.BuildLines(
            time, expenses, flatFee is null ? null : (decimal)flatFee.Value, flatFeeDescription, (decimal)hourlyRate);
        if (draft.Count == 0)
        {
            return $"Nothing unbilled on matter '{matter.Name}' in that period — no time, no expenses, no flat fee. " +
                   "Log time with log_time or the timer, record expenses with record_expense, or pass a flatFee.";
        }

        var year = to.Year;
        var existing = await db.Invoices.Select(i => i.Number).ToListAsync(cancellationToken);
        var number = Billing.NextInvoiceNumber(year, existing);

        var invoice = new Invoice
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            Number = number,
            Status = InvoiceStatus.Draft,
            PeriodStart = from,
            PeriodEnd = to,
            PreparedByUserId = currentUser.UserId,
            PreparedByDisplay = currentUser.DisplayName,
            Total = draft.Sum(l => l.Amount),
        };

        foreach (var line in draft)
        {
            invoice.Lines.Add(new InvoiceLine
            {
                TenantId = invoice.TenantId,
                Kind = line.Kind,
                Description = line.Description,
                Quantity = line.Quantity,
                Rate = line.Rate,
                Amount = line.Amount,
                SourceId = line.SourceId,
                OccurredOn = line.OccurredOn,
            });
        }

        db.Invoices.Add(invoice);

        // Stamp the sources in the SAME save as the invoice. The id is already there to stamp with
        // (EntityBase assigns a v7 GUID at construction, not at insert), so there is no reason to
        // commit the invoice first — and every reason not to. Two saves means a window where the
        // invoice is committed and its sources are not, and the bill-once guarantee is precisely
        // that stamp: the next draft would sweep those hours up again and bill the client twice.
        // One SaveChangesAsync is one transaction, so the crash leaves nothing rather than half.
        foreach (var entry in time)
        {
            entry.InvoiceId = invoice.Id;
        }

        foreach (var expense in expenses)
        {
            expense.InvoiceId = invoice.Id;
        }

        await db.SaveChangesAsync(cancellationToken);

        var hours = time.Sum(t => t.Hours);
        return $"Drafted invoice {number} for matter '{matter.Name}' ({Billing.PeriodLabel(from, to)}): " +
               $"{Billing.Money(invoice.Total)} across {draft.Count} line(s) — " +
               $"{hours:0.##}h time, {expenses.Count} expense(s)" +
               $"{(flatFee is null ? "" : $", flat fee {Billing.Money((decimal)flatFee.Value)}")}. " +
               $"Prepared by {currentUser.DisplayName ?? "you"}; it must be approved by someone else " +
               "(approve_invoice) before it can be sent.";
    }

    [Description("Approve a DRAFT invoice so it can be sent. Separation of duties: the person who drafted it can NEVER approve it — that approval is refused outright. Side-effecting and requires human approval.")]
    public async Task<string> ApproveInvoice(
        [Description("The invoice number, e.g. INV-2026-0001.")] string invoiceNumber,
        CancellationToken cancellationToken = default)
    {
        var invoice = await FindInvoiceAsync(invoiceNumber, cancellationToken);
        if (invoice is null)
        {
            return $"No invoice numbered '{invoiceNumber}' exists. Use list_invoices to find it.";
        }

        if (invoice.Status != InvoiceStatus.Draft)
        {
            return $"Invoice {invoice.Number} is {invoice.Status.ToString().ToLowerInvariant()}, not a draft — " +
                   "only a draft can be approved.";
        }

        // THE control. A firm's engagement letter promises the client that nobody approves their
        // own bill; enforcing it anywhere but here would make it advisory.
        if (!Billing.CanApprove(invoice.PreparedByUserId, currentUser.UserId))
        {
            return $"REFUSED: {invoice.PreparedByDisplay ?? "you"} prepared invoice {invoice.Number} and " +
                   "cannot approve it. Separation of duties requires a second person to approve a bill " +
                   "before it goes to the client — ask a colleague with billing rights to approve it.";
        }

        invoice.Status = InvoiceStatus.Approved;
        invoice.ApprovedByUserId = currentUser.UserId;
        invoice.ApprovedByDisplay = currentUser.DisplayName;
        invoice.ApprovedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var matterName = await MatterNameAsync(invoice.MatterId, cancellationToken);
        return $"Approved invoice {invoice.Number} for matter '{matterName}' ({Billing.Money(invoice.Total)}) — " +
               $"prepared by {invoice.PreparedByDisplay ?? "unknown"}, approved by " +
               $"{currentUser.DisplayName ?? "you"} at {invoice.ApprovedAt:yyyy-MM-dd HH:mm} UTC. " +
               "It can now be sent with send_invoice.";
    }

    [Description("Mark an APPROVED invoice as sent to the client. Refused for a draft — an unapproved bill must never leave the firm. Outward-facing and requires human approval.")]
    public async Task<string> SendInvoice(
        [Description("The invoice number, e.g. INV-2026-0001.")] string invoiceNumber,
        CancellationToken cancellationToken = default)
    {
        var invoice = await FindInvoiceAsync(invoiceNumber, cancellationToken);
        if (invoice is null)
        {
            return $"No invoice numbered '{invoiceNumber}' exists. Use list_invoices to find it.";
        }

        if (invoice.Status == InvoiceStatus.Draft)
        {
            return $"REFUSED: invoice {invoice.Number} is still a draft. An unapproved bill must not leave " +
                   "the firm — have someone other than its preparer approve it first (approve_invoice).";
        }

        if (invoice.Status != InvoiceStatus.Approved)
        {
            return $"Invoice {invoice.Number} is {invoice.Status.ToString().ToLowerInvariant()} — only an " +
                   "approved invoice can be sent.";
        }

        invoice.Status = InvoiceStatus.Sent;
        invoice.SentAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var matterName = await MatterNameAsync(invoice.MatterId, cancellationToken);
        return $"Invoice {invoice.Number} for matter '{matterName}' ({Billing.Money(invoice.Total)}) is marked " +
               $"sent as of {invoice.SentAt:yyyy-MM-dd}. Casewell records the dispatch; it does not itself " +
               "email the client.";
    }

    [Description("List invoices — one matter's, or the firm's most recent — with status and totals.")]
    public async Task<string> ListInvoices(
        [Description("Optional matter name; omit for the firm's recent invoices.")] string? matterName = null,
        CancellationToken cancellationToken = default)
    {
        Guid? matterId = null;
        var label = "the firm";
        if (!string.IsNullOrWhiteSpace(matterName))
        {
            var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
            if (matter is null)
            {
                return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
            }

            matterId = matter.Id;
            label = $"matter '{matter.Name}'";
        }

        var rows = await db.Invoices
            .Where(i => matterId == null || i.MatterId == matterId)
            .Join(db.Matters, i => i.MatterId, m => m.Id, (i, m) => new
            {
                i.Number, i.Status, i.Total, i.PeriodEnd, i.PreparedByDisplay, i.ApprovedByDisplay,
                MatterName = m.Name, m.RestrictedUserIdsJson,
            })
            .OrderByDescending(i => i.Number)
            .Take(100)
            .ToListAsync(cancellationToken);

        // Walls hide names, never the fact that a bill exists — the check_conflicts convention.
        var visible = rows
            .Select(r => new
            {
                r.Number, r.Status, r.Total, r.PeriodEnd, r.PreparedByDisplay, r.ApprovedByDisplay,
                Label = Matter.WallAllows(r.RestrictedUserIdsJson, currentUser.UserId)
                    ? r.MatterName
                    : TrustAccounting.RestrictedLabel(null),
            })
            .ToList();

        if (visible.Count == 0)
        {
            return $"No invoices for {label} yet. Draft one with draft_invoice.";
        }

        var sb = new StringBuilder($"Invoices — {label}:\n");
        foreach (var i in visible)
        {
            sb.AppendLine($"- {i.Number} · {i.Label} · {i.Status.ToString().ToLowerInvariant(),-8} · " +
                          $"{Billing.Money(i.Total),12} · through {i.PeriodEnd:yyyy-MM-dd}" +
                          $" · prepared by {i.PreparedByDisplay ?? "unknown"}" +
                          $"{(i.ApprovedByDisplay is null ? "" : $", approved by {i.ApprovedByDisplay}")}");
        }

        sb.Append($"Total: {Billing.Money(visible.Sum(i => i.Total))} across {visible.Count} invoice(s).");
        return sb.ToString();
    }

    [Description("One invoice in full: every line with hours, rate, and amount, plus who prepared and approved it.")]
    public async Task<string> GetInvoice(
        [Description("The invoice number, e.g. INV-2026-0001.")] string invoiceNumber,
        CancellationToken cancellationToken = default)
    {
        var invoice = await FindInvoiceAsync(invoiceNumber, cancellationToken);
        if (invoice is null)
        {
            return $"No invoice numbered '{invoiceNumber}' exists. Use list_invoices to find it.";
        }

        var lines = await db.InvoiceLines
            .Where(l => l.InvoiceId == invoice.Id)
            .OrderBy(l => l.Kind).ThenBy(l => l.OccurredOn)
            .ToListAsync(cancellationToken);
        var matterName = await MatterNameAsync(invoice.MatterId, cancellationToken);

        var sb = new StringBuilder(
            $"Invoice {invoice.Number} — matter '{matterName}' " +
            $"({invoice.Status.ToString().ToLowerInvariant()}, {Billing.PeriodLabel(invoice.PeriodStart, invoice.PeriodEnd)}):\n");
        foreach (var l in lines)
        {
            var detail = l.Kind == InvoiceLineKind.Time
                ? $"{l.Quantity:0.##}h @ {Billing.Money(l.Rate ?? 0m)}"
                : l.Kind.ToString().ToLowerInvariant();
            sb.AppendLine($"- {l.OccurredOn:yyyy-MM-dd} · {detail,-22} · {Billing.Money(l.Amount),12} · {l.Description}");
        }

        sb.AppendLine($"Total: {Billing.Money(invoice.Total)}.");
        sb.Append($"Prepared by {invoice.PreparedByDisplay ?? "unknown"}" +
                  (invoice.ApprovedByDisplay is null
                      ? " — not yet approved (and its preparer may not approve it)."
                      : $"; approved by {invoice.ApprovedByDisplay} at {invoice.ApprovedAt:yyyy-MM-dd HH:mm} UTC."));
        return sb.ToString();
    }

    private async Task<RunningTimer?> CurrentTimerAsync(CancellationToken cancellationToken) =>
        await db.RunningTimers.FirstOrDefaultAsync(t => t.UserId == currentUser.UserId, cancellationToken);

    private async Task<string> MatterNameAsync(Guid matterId, CancellationToken cancellationToken) =>
        (await db.Matters.FirstOrDefaultAsync(m => m.Id == matterId, cancellationToken))?.Name ?? "(unknown)";

    private async Task<Invoice?> FindInvoiceAsync(string number, CancellationToken cancellationToken)
    {
        var normalized = number.Trim();
        var invoice = await db.Invoices.FirstOrDefaultAsync(
            i => EF.Functions.ILike(i.Number, normalized), cancellationToken);
        if (invoice is null)
        {
            return null;
        }

        var matter = await db.Matters.FirstOrDefaultAsync(m => m.Id == invoice.MatterId, cancellationToken);
        return matter is not null && matter.IsAccessibleTo(currentUser.UserId) ? invoice : null;
    }

    private async Task<Matter?> FindAccessibleMatterAsync(string name, CancellationToken cancellationToken)
    {
        var normalized = name.Trim();
        var matter = await db.Matters.FirstOrDefaultAsync(
            m => EF.Functions.ILike(m.Name, normalized), cancellationToken);
        return matter is not null && matter.IsAccessibleTo(currentUser.UserId) ? matter : null;
    }
}

/// <summary>A line the draft builder produced, before it becomes an <see cref="InvoiceLine"/> row.</summary>
public sealed record DraftLine(
    InvoiceLineKind Kind,
    string Description,
    decimal? Quantity,
    decimal? Rate,
    decimal Amount,
    Guid? SourceId,
    DateOnly OccurredOn);

/// <summary>Pure billing rules behind the tools, unit-tested without a database.</summary>
public static class Billing
{
    /// <summary>
    /// UTBMS-style litigation/counselling activity codes, with the words that imply each. Small on
    /// purpose: a code the firm won't recognise is worse than none, and an uncoded entry is the one
    /// a client challenges.
    /// </summary>
    public static readonly IReadOnlyList<(string Code, string Label, string[] Keywords)> ActivityCodes =
    [
        ("L110", "Fact investigation / development", ["investigat", "fact", "witness", "evidence", "diligence"]),
        ("L120", "Analysis / strategy", ["analys", "analyz", "strategy", "assess", "advise", "advice", "research"]),
        ("L140", "Document / file management", ["file", "organiz", "index", "document management"]),
        ("L190", "Other case assessment", ["review", "read"]),
        ("L210", "Pleadings", ["pleading", "complaint", "answer", "motion", "brief", "petition"]),
        ("L230", "Court mandatory conferences", ["hearing", "conference", "court appearance", "appear"]),
        ("L240", "Dispositive motions", ["summary judgment", "dismiss"]),
        ("L250", "Other written motions", ["stipulation", "notice"]),
        ("L310", "Written discovery", ["discovery", "interrogator", "subpoena", "request for production"]),
        ("L330", "Depositions", ["deposition", "depose"]),
        ("L390", "Other discovery", ["expert"]),
        ("L430", "Written motions / submissions", ["submission"]),
        ("A103", "Draft / revise", ["draft", "revis", "prepar", "redlin", "amend", "nda", "agreement", "contract"]),
        ("A104", "Review / analyze", ["review"]),
        ("A105", "Communicate in firm", ["internal", "colleague", "team meeting"]),
        ("A106", "Communicate with client", ["client call", "call with client", "client meeting", "update the client", "client email"]),
        ("A107", "Communicate with opposing counsel", ["opposing", "counterparty", "other side"]),
        ("A108", "Communicate with others", ["call", "email", "meeting", "correspond", "phone"]),
        ("A111", "Other", ["travel", "admin"]),
    ];

    /// <summary>Positive and under a billion — beyond that it's a typo, not a bill.</summary>
    public static bool AmountIsValid(double amount) =>
        !double.IsNaN(amount) && !double.IsInfinity(amount) && amount is > 0 and <= 1_000_000_000;

    /// <summary>Positive and under 100,000/hour — no firm bills more than that.</summary>
    public static bool RateIsValid(double rate) =>
        !double.IsNaN(rate) && !double.IsInfinity(rate) && rate is > 0 and <= 100_000;

    /// <summary>
    /// The separation-of-duties rule: an invoice's preparer may never approve it. An unknown
    /// preparer is also refused — a bill with no attributable author must not slip through on a
    /// null, which is the failure mode a permissive default would create.
    /// </summary>
    public static bool CanApprove(Guid? preparedBy, Guid? approver) =>
        preparedBy is not null && approver is not null && preparedBy != approver;

    /// <summary>Wall-clock since the timer started, floored at zero against a clock skew.</summary>
    public static TimeSpan Elapsed(DateTimeOffset startedAt)
    {
        var elapsed = DateTimeOffset.UtcNow - startedAt;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    /// <summary>
    /// Elapsed time as billable hours, rounded UP to the next tenth of an hour — the six-minute
    /// increment firms bill in. Any started increment bills as a whole one, and a timer stopped
    /// almost immediately still bills the 0.1h minimum rather than zero.
    /// </summary>
    public static decimal ElapsedToHours(TimeSpan elapsed)
    {
        var tenths = Math.Ceiling((decimal)elapsed.TotalHours * 10m);
        return Math.Max(0.1m, tenths / 10m);
    }

    /// <summary>"1h 24m" / "12m" — for the running-timer readout.</summary>
    public static string ElapsedLabel(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m"
            : $"{Math.Max(1, (int)Math.Ceiling(elapsed.TotalMinutes))}m";

    /// <summary>A known activity code, upper-cased — or null when it isn't one we publish.</summary>
    public static string? NormalizeActivityCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var candidate = code.Trim().ToUpperInvariant();
        return ActivityCodes.Any(a => a.Code == candidate) ? candidate : null;
    }

    /// <summary>The label for a code, for readable tool output.</summary>
    public static string ActivityLabel(string code) =>
        ActivityCodes.FirstOrDefault(a => a.Code == code).Label ?? "";

    /// <summary>
    /// Suggest an activity code from a narrative, so an entry captured in a hurry is still coded.
    /// Longest keyword wins, so "call with client" beats the bare "call". Null when nothing matches
    /// — a wrong code is worse than none.
    /// </summary>
    public static string? SuggestActivityCode(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        var text = description.ToLowerInvariant();
        return ActivityCodes
            .SelectMany(a => a.Keywords.Select(k => (a.Code, Keyword: k)))
            .Where(x => text.Contains(x.Keyword, StringComparison.Ordinal))
            .OrderByDescending(x => x.Keyword.Length)
            .Select(x => x.Code)
            .FirstOrDefault();
    }

    /// <summary>"INV-2026-0001" — per-year sequence, continuing from the highest existing number.</summary>
    public static string NextInvoiceNumber(int year, IEnumerable<string> existingNumbers)
    {
        var prefix = $"INV-{year}-";
        var highest = existingNumbers
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(n => int.TryParse(n[prefix.Length..], out var seq) ? seq : 0)
            .DefaultIfEmpty(0)
            .Max();
        return $"{prefix}{highest + 1:0000}";
    }

    /// <summary>"inception – 2026-07-31" or "2026-07-01 – 2026-07-31".</summary>
    public static string PeriodLabel(DateOnly? from, DateOnly to) =>
        $"{(from is null ? "inception" : from.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))} – {to:yyyy-MM-dd}";

    /// <summary>"$1,234.50" (invariant) — shares the trust ledger's formatting.</summary>
    public static string Money(decimal amount) => TrustAccounting.Money(amount);

    /// <summary>
    /// The one-click draft: every unbilled billable hour at its own agreed rate (or the invoice's),
    /// every recoverable expense at cost, then the flat fee as its own line. Amounts round to the
    /// cent per line, so the stored total always equals what the client can add up.
    /// </summary>
    public static IReadOnlyList<DraftLine> BuildLines(
        IEnumerable<TimeEntry> time,
        IEnumerable<Expense> expenses,
        decimal? flatFee,
        string? flatFeeDescription,
        decimal defaultRate)
    {
        var lines = new List<DraftLine>();

        foreach (var t in time)
        {
            var rate = t.Rate ?? defaultRate;
            lines.Add(new DraftLine(
                InvoiceLineKind.Time,
                t.ActivityCode is null ? t.Description : $"[{t.ActivityCode}] {t.Description}",
                t.Hours,
                rate,
                Math.Round(t.Hours * rate, 2, MidpointRounding.AwayFromZero),
                t.Id,
                t.WorkedOn));
        }

        foreach (var e in expenses)
        {
            lines.Add(new DraftLine(
                InvoiceLineKind.Expense,
                e.Description,
                null,
                null,
                Math.Round(e.Amount, 2, MidpointRounding.AwayFromZero),
                e.Id,
                e.IncurredOn));
        }

        if (flatFee is not null)
        {
            lines.Add(new DraftLine(
                InvoiceLineKind.FlatFee,
                string.IsNullOrWhiteSpace(flatFeeDescription) ? "Flat fee" : flatFeeDescription.Trim(),
                null,
                null,
                Math.Round(flatFee.Value, 2, MidpointRounding.AwayFromZero),
                null,
                DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime)));
        }

        return lines;
    }
}
