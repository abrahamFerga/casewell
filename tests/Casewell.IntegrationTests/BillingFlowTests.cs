using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Cortex.Modules.Legal;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casewell.IntegrationTests;

/// <summary>
/// Issue #19's three acceptance criteria against the real host and a real Postgres.
/// <para>
/// The platform-level approval gate on these tools is asserted over HTTP (<see cref="Gate"/>
/// region); the module's own rules — timer rounding, the one-click sweep, and the
/// separation-of-duties refusal — are asserted through <c>AuthorizedScopeAsync</c>, because the
/// Mock provider fills tool arguments by naive name matching and cannot drive a multi-argument
/// billing flow deterministically.
/// </para>
/// </summary>
[Collection("api")]
public sealed class BillingFlowTests(IntegrationFixture fixture)
{
    // ─────────── AC1: timer start/stop, scoped to matter + activity code ───────────

    [Fact]
    public async Task Timer_start_then_stop_logs_an_entry_scoped_to_matter_and_activity_code()
    {
        var matter = await SeedMatterAsync("Timer Co - Formation");
        var (scope, _, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var billing = scope.ServiceProvider.GetRequiredService<BillingTools>();
        var db = scope.ServiceProvider.GetRequiredService<LegalDbContext>();

        var started = await billing.StartTimer("Timer Co - Formation", "Drafted the NDA");
        Assert.Contains("Timer started", started);
        Assert.Contains("A103", started);   // suggested from "Drafted"

        var status = await billing.TimerStatus();
        Assert.Contains("Timer running on matter 'Timer Co - Formation'", status);

        var stopped = await billing.StopTimer();
        Assert.Contains("logged", stopped);

        var entry = await db.TimeEntries
            .Where(t => t.MatterId == matter && t.Description == "Drafted the NDA")
            .SingleAsync();
        Assert.Equal("A103", entry.ActivityCode);
        Assert.True(entry.Hours >= 0.1m, "a stopped timer bills at least the 0.1h minimum");
        Assert.True(entry.Billable);

        // The timer is consumed, not left running.
        Assert.Empty(await db.RunningTimers.Where(t => t.MatterId == matter).ToListAsync());
    }

    [Fact]
    public async Task Starting_a_second_timer_is_refused_so_the_first_is_not_lost()
    {
        await SeedMatterAsync("Double Timer Co - Matter");
        var (scope, _, _) = await fixture.AuthorizedScopeAsync(subject: "double-timer-user");
        using var _scope = scope;
        var billing = scope.ServiceProvider.GetRequiredService<BillingTools>();

        await billing.StartTimer("Double Timer Co - Matter", "First task");
        var second = await billing.StartTimer("Double Timer Co - Matter", "Second task");

        Assert.Contains("REFUSED", second);
        Assert.Contains("already running", second);

        await billing.StopTimer();   // leave no timer behind for other tests
    }

    // ─────────── AC2: one-click invoice draft from time + expenses + flat fees ───────────

    [Fact]
    public async Task Draft_invoice_sweeps_time_expenses_and_a_flat_fee_into_one_invoice()
    {
        var matterId = await SeedMatterAsync("Sweep Co - Litigation");
        var (scope, tenantId, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var billing = scope.ServiceProvider.GetRequiredService<BillingTools>();
        var db = scope.ServiceProvider.GetRequiredService<LegalDbContext>();

        db.TimeEntries.Add(new TimeEntry
        {
            TenantId = tenantId, MatterId = matterId, Hours = 2m, Description = "Reviewed the complaint",
            WorkedOn = DateOnly.FromDateTime(DateTime.UtcNow), Billable = true, ActivityCode = "L190",
        });
        await db.SaveChangesAsync();

        await billing.RecordExpense("Sweep Co - Litigation", 402, "Court filing fee");

        var result = await billing.DraftInvoice(
            "Sweep Co - Litigation", hourlyRate: 350,
            flatFee: 2500, flatFeeDescription: "Fixed fee for the motion");

        Assert.Contains("Drafted invoice INV-", result);

        var invoice = await db.Invoices.Where(i => i.MatterId == matterId).SingleAsync();
        var lines = await db.InvoiceLines.Where(l => l.InvoiceId == invoice.Id).ToListAsync();

        // All three sources, in one step.
        Assert.Equal(3, lines.Count);
        Assert.Contains(lines, l => l.Kind == InvoiceLineKind.Time && l.Amount == 700m);      // 2h × 350
        Assert.Contains(lines, l => l.Kind == InvoiceLineKind.Expense && l.Amount == 402m);
        Assert.Contains(lines, l => l.Kind == InvoiceLineKind.FlatFee && l.Amount == 2500m);
        Assert.Equal(3602m, invoice.Total);
        Assert.Equal(InvoiceStatus.Draft, invoice.Status);
    }

    [Fact]
    public async Task A_second_draft_cannot_rebill_time_the_first_already_billed()
    {
        var matterId = await SeedMatterAsync("Bill Once Co - Matter");
        var (scope, tenantId, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var billing = scope.ServiceProvider.GetRequiredService<BillingTools>();
        var db = scope.ServiceProvider.GetRequiredService<LegalDbContext>();

        db.TimeEntries.Add(new TimeEntry
        {
            TenantId = tenantId, MatterId = matterId, Hours = 1m, Description = "Billable once",
            WorkedOn = DateOnly.FromDateTime(DateTime.UtcNow), Billable = true,
        });
        await db.SaveChangesAsync();

        await billing.DraftInvoice("Bill Once Co - Matter", hourlyRate: 300);
        var second = await billing.DraftInvoice("Bill Once Co - Matter", hourlyRate: 300);

        // The InvoiceId stamp is what makes double-billing impossible even on an overlapping period.
        Assert.Contains("Nothing unbilled", second);
        Assert.Single(await db.Invoices.Where(i => i.MatterId == matterId).ToListAsync());
    }

    /// <summary>
    /// Bill-once has to survive a crash, not only the happy path.
    /// <see cref="A_second_draft_cannot_rebill_time_the_first_already_billed"/> walks the path where
    /// nothing goes wrong, so it passes whether or not the draft is atomic. This one drives the
    /// failure: a context whose <b>second</b> save throws, which is exactly the window between
    /// committing the invoice and stamping the rows it was assembled from. If a draft leaves an
    /// invoice behind whose sources are still unstamped, the next draft bills those hours again —
    /// and the firm sends the client the same hour twice.
    /// <para>
    /// Note what green means here: once the draft is a single save, the injected failure on save #2
    /// never fires, so the crash window has been closed rather than survived. Re-introduce the
    /// second save and this test goes red again, which is the regression it exists to guard.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_crash_between_the_invoice_and_its_stamps_cannot_rebill_the_same_hour()
    {
        var matterId = await SeedMatterAsync("Atomic Draft Co - Matter");
        var (scope, tenantId, _) = await fixture.AuthorizedScopeAsync(subject: "atomic-draft-user");
        using var _scope = scope;
        var db = scope.ServiceProvider.GetRequiredService<LegalDbContext>();

        db.TimeEntries.Add(new TimeEntry
        {
            TenantId = tenantId, MatterId = matterId, Hours = 4m, Description = "Billed exactly once",
            WorkedOn = DateOnly.FromDateTime(DateTime.UtcNow), Billable = true,
        });
        await db.SaveChangesAsync();

        var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        var currentUser = scope.ServiceProvider.GetRequiredService<ICurrentUser>();
        var faultyOptions = new DbContextOptionsBuilder<LegalDbContext>(
                scope.ServiceProvider.GetRequiredService<DbContextOptions<LegalDbContext>>())
            .AddInterceptors(new FailOnNthSave(2))
            .Options;

        await using (var faultyDb = new LegalDbContext(faultyOptions, tenantContext))
        {
            var crashing = new BillingTools(faultyDb, tenantContext, currentUser);
            try
            {
                await crashing.DraftInvoice("Atomic Draft Co - Matter", hourlyRate: 500);
            }
            catch (InvalidOperationException e) when (e.Message.Contains(FailOnNthSave.Marker))
            {
                // The injected crash. What matters is not that it threw — it is what it left behind.
            }
        }

        // Whatever the crash left, the next draft must not be able to bill that hour a second time.
        await scope.ServiceProvider.GetRequiredService<BillingTools>()
            .DraftInvoice("Atomic Draft Co - Matter", hourlyRate: 500);

        db.ChangeTracker.Clear();
        var entryId = (await db.TimeEntries.SingleAsync(t => t.MatterId == matterId)).Id;
        var invoiceIds = await db.Invoices.Where(i => i.MatterId == matterId).Select(i => i.Id).ToListAsync();
        var timesBilled = await db.InvoiceLines
            .Where(l => invoiceIds.Contains(l.InvoiceId) && l.SourceId == entryId)
            .CountAsync();

        Assert.True(timesBilled == 1,
            $"the same hour reached {timesBilled} invoice(s) across {invoiceIds.Count} draft(s) — " +
            "a draft that commits the invoice before stamping its sources is not atomic, so a " +
            "partial failure leaves the sources unbilled and the next draft rebills them");
    }

    /// <summary>Fails the Nth <c>SaveChanges</c> on a context, to stand in for a crash mid-operation.</summary>
    private sealed class FailOnNthSave(int failOn) : SaveChangesInterceptor
    {
        public const string Marker = "injected crash (FailOnNthSave)";

        private int _saves;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref _saves) == failOn
                ? throw new InvalidOperationException($"{Marker}: save #{failOn} never reached the database")
                : base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    // ─────────── AC3: approval blocks self-approval, and approval is recorded ───────────

    [Fact]
    public async Task The_preparer_can_never_approve_their_own_invoice()
    {
        var matterId = await SeedMatterAsync("Self Approve Co - Matter");
        var (preparerScope, tenantId, preparerId) =
            await fixture.AuthorizedScopeAsync(subject: "preparer-attorney", displayName: "Preparer Attorney");
        using var _preparer = preparerScope;
        var preparerBilling = preparerScope.ServiceProvider.GetRequiredService<BillingTools>();
        var db = preparerScope.ServiceProvider.GetRequiredService<LegalDbContext>();

        db.TimeEntries.Add(new TimeEntry
        {
            TenantId = tenantId, MatterId = matterId, Hours = 3m, Description = "Prepared the case",
            WorkedOn = DateOnly.FromDateTime(DateTime.UtcNow), Billable = true,
        });
        await db.SaveChangesAsync();

        await preparerBilling.DraftInvoice("Self Approve Co - Matter", hourlyRate: 400);
        var invoice = await db.Invoices.Where(i => i.MatterId == matterId).SingleAsync();
        Assert.Equal(preparerId, invoice.PreparedByUserId);

        // The preparer tries to approve their own bill.
        var refused = await preparerBilling.ApproveInvoice(invoice.Number);

        Assert.Contains("REFUSED", refused);
        Assert.Contains("cannot approve it", refused);

        await db.Entry(invoice).ReloadAsync();
        Assert.Equal(InvoiceStatus.Draft, invoice.Status);
        Assert.Null(invoice.ApprovedByUserId);
        Assert.Null(invoice.ApprovedAt);
    }

    [Fact]
    public async Task A_different_user_can_approve_and_the_approval_is_recorded_then_sendable()
    {
        var matterId = await SeedMatterAsync("Two Person Co - Matter");
        var (preparerScope, tenantId, preparerId) =
            await fixture.AuthorizedScopeAsync(subject: "two-person-preparer", displayName: "Drafting Associate");
        using var _preparer = preparerScope;
        var preparerBilling = preparerScope.ServiceProvider.GetRequiredService<BillingTools>();
        var db = preparerScope.ServiceProvider.GetRequiredService<LegalDbContext>();

        db.TimeEntries.Add(new TimeEntry
        {
            TenantId = tenantId, MatterId = matterId, Hours = 1.5m, Description = "Associate work",
            WorkedOn = DateOnly.FromDateTime(DateTime.UtcNow), Billable = true,
        });
        await db.SaveChangesAsync();

        await preparerBilling.DraftInvoice("Two Person Co - Matter", hourlyRate: 200);
        var number = (await db.Invoices.Where(i => i.MatterId == matterId).SingleAsync()).Number;

        // A genuinely different user — a different dev subject provisions a different user row.
        var (approverScope, _, approverId) =
            await fixture.AuthorizedScopeAsync(subject: "two-person-partner", displayName: "Reviewing Partner");
        using var _approver = approverScope;
        var approverBilling = approverScope.ServiceProvider.GetRequiredService<BillingTools>();
        var approverDb = approverScope.ServiceProvider.GetRequiredService<LegalDbContext>();
        Assert.NotEqual(preparerId, approverId);

        // Sending before approval is refused — an unapproved bill must not leave the firm.
        var tooEarly = await approverBilling.SendInvoice(number);
        Assert.Contains("REFUSED", tooEarly);
        Assert.Contains("still a draft", tooEarly);

        var approved = await approverBilling.ApproveInvoice(number);
        Assert.Contains("Approved invoice", approved);
        Assert.Contains("Reviewing Partner", approved);

        var invoice = await approverDb.Invoices.Where(i => i.Number == number).SingleAsync();
        Assert.Equal(InvoiceStatus.Approved, invoice.Status);
        Assert.Equal(approverId, invoice.ApprovedByUserId);
        Assert.Equal(preparerId, invoice.PreparedByUserId);
        Assert.NotNull(invoice.ApprovedAt);

        var sent = await approverBilling.SendInvoice(number);
        Assert.Contains("marked sent", sent);
    }

    // ─────────── Gate: the platform-level surface, asserted over real HTTP ───────────

    [Fact]
    public async Task Billing_tools_are_registered_with_the_approval_flags_they_claim()
    {
        using var client = fixture.AdminClient();

        var catalog = await client.GetFromJsonAsync<JsonElement>("/api/admin/security/catalog");
        var tools = catalog.GetProperty("modules").EnumerateArray()
            .Single(m => m.GetProperty("id").GetString() == "legal")
            .GetProperty("tools").EnumerateArray()
            .ToDictionary(
                t => t.GetProperty("permission").GetString()!,
                t => t.GetProperty("requiresApproval").GetBoolean());

        // Anything that touches a client's bill is gated.
        foreach (var gated in new[] { "record_expense", "draft_invoice", "approve_invoice", "send_invoice" })
        {
            Assert.True(tools.TryGetValue($"tools.legal.{gated}", out var requires),
                $"{gated} is missing from the catalog — manifest and IModuleToolSource disagree");
            Assert.True(requires, $"{gated} must require approval");
        }

        // Quick time capture is the deliberate exception; reads are never gated.
        foreach (var open in new[] { "start_timer", "stop_timer", "timer_status", "list_invoices", "get_invoice" })
        {
            Assert.True(tools.TryGetValue($"tools.legal.{open}", out var requires),
                $"{open} is missing from the catalog");
            Assert.False(requires, $"{open} must not require approval");
        }
    }

    [Fact]
    public async Task A_draft_invoice_turn_is_intercepted_by_the_approval_gate()
    {
        await SeedMatterAsync("Gated Draft Co - Matter");
        using var client = fixture.AdminClient();

        var response = await client.PostAsJsonAsync("/api/agui/legal", new
        {
            messages = new[]
            {
                new { id = "m1", role = "user", content = "Draft an invoice for Gated Draft Co - Matter" },
            },
        });
        response.EnsureSuccessStatusCode();

        var events = (await response.Content.ReadAsStringAsync())
            .Split('\n')
            .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
            .Select(l => JsonSerializer.Deserialize<JsonElement>(l["data:".Length..].Trim()))
            .ToList();

        Assert.Contains(events, e =>
            e.GetProperty("type").GetString() == "TOOL_CALL_START" &&
            e.GetProperty("toolCallName").GetString() == "draft_invoice");
        Assert.Contains(events, e =>
            e.GetProperty("type").GetString() == "CUSTOM" &&
            e.GetProperty("name").GetString() == "approval_required");
        Assert.DoesNotContain("RUN_ERROR", events.Select(e => e.GetProperty("type").GetString()));

        // Nothing was written: the gate intercepts before the tool runs.
        var pending = await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals");
        Assert.Contains(pending.EnumerateArray(), a =>
            a.GetProperty("toolName").GetString() == "draft_invoice");
    }

    [Fact]
    public async Task The_invoice_and_expense_tab_endpoints_answer_for_a_matter_viewer()
    {
        var matterId = await SeedMatterAsync("Tab Endpoint Co - Matter");
        using var client = fixture.AdminClient();

        var (scope, tenantId, _) = await fixture.AuthorizedScopeAsync();
        using var _scope = scope;
        var billing = scope.ServiceProvider.GetRequiredService<BillingTools>();
        var db = scope.ServiceProvider.GetRequiredService<LegalDbContext>();

        await billing.RecordExpense("Tab Endpoint Co - Matter", 87.50, "Courier to the court");
        db.TimeEntries.Add(new TimeEntry
        {
            TenantId = tenantId, MatterId = matterId, Hours = 1m, Description = "Tab work",
            WorkedOn = DateOnly.FromDateTime(DateTime.UtcNow), Billable = true,
        });
        await db.SaveChangesAsync();
        await billing.DraftInvoice("Tab Endpoint Co - Matter", hourlyRate: 250);

        var invoices = await client.GetFromJsonAsync<JsonElement>("/api/legal/invoices");
        Assert.Contains(invoices.EnumerateArray(), i =>
            i.GetProperty("matterName").GetString() == "Tab Endpoint Co - Matter" &&
            i.GetProperty("status").GetString() == "draft");

        var expenses = await client.GetFromJsonAsync<JsonElement>("/api/legal/expenses");
        Assert.Contains(expenses.EnumerateArray(), e =>
            e.GetProperty("description").GetString() == "Courier to the court" &&
            e.GetProperty("billed").GetString() == "billed");   // the draft swept it up
    }

    [Fact]
    public async Task An_invoice_approval_is_gated_and_the_gate_lands_in_the_append_only_audit()
    {
        // AC3's second half, and the only place any of it can be proven. The module writes no audit
        // of its own: the tool-call audit belongs to the platform and is written by the invocation
        // middleware. AuthorizedScopeAsync bypasses that middleware by design (see its remarks), so
        // no scope-based test can establish this — the approval has to travel over HTTP.
        //
        // What this proves: approve_invoice is gated, and the interception is audited under its own
        // permission string. What it deliberately does NOT prove: that a SUCCESSFUL approval is
        // audited. The Mock provider fills a tool's arguments by naive name matching and hands the
        // whole user message to the single string parameter, so the released call always refuses
        // with a bad invoice number — the same limitation this class's remarks already record.
        // Proving the success path needs a provider that can fill an argument; see TODO(plenipo#140).
        var matterId = await SeedMatterAsync("Audited Approval Co - Matter");

        // Someone else drafts it, so the admin who approves is a lawful approver.
        var (preparerScope, tenantId, _) = await fixture.AuthorizedScopeAsync(
            subject: "audited-approval-preparer", displayName: "Drafting Associate");
        using var _preparer = preparerScope;
        var preparerBilling = preparerScope.ServiceProvider.GetRequiredService<BillingTools>();
        var db = preparerScope.ServiceProvider.GetRequiredService<LegalDbContext>();

        db.TimeEntries.Add(new TimeEntry
        {
            TenantId = tenantId, MatterId = matterId, Hours = 2m, Description = "Associate work",
            WorkedOn = DateOnly.FromDateTime(DateTime.UtcNow), Billable = true,
        });
        await db.SaveChangesAsync();
        await preparerBilling.DraftInvoice("Audited Approval Co - Matter", hourlyRate: 300);
        var number = (await db.Invoices.Where(i => i.MatterId == matterId).SingleAsync()).Number;

        using var client = fixture.AdminClient();

        // Before: however many approvals earlier tests logged, this one is not among them.
        var before = await ApprovalAuditAsync(client);

        var response = await client.PostAsJsonAsync("/api/agui/legal", new
        {
            messages = new[]
            {
                new { id = "m1", role = "user", content = $"Approve invoice {number}" },
            },
        });
        response.EnsureSuccessStatusCode();

        // The gate intercepts it — approving a bill is never self-service.
        var pending = await client.GetFromJsonAsync<JsonElement>("/api/chat/approvals");
        var call = pending.EnumerateArray()
            .Single(a => a.GetProperty("toolName").GetString() == "approve_invoice");

        // The refusal is itself on the record — the audit gains a FAILED call carrying the reason,
        // and no successful one. "Someone tried to approve a bill and was held" is exactly what a
        // bar audit needs to see, so this row is evidence, not noise.
        var atInterception = await ApprovalAuditAsync(client);
        Assert.Equal(before.Succeeded, atInterception.Succeeded);
        Assert.Equal(before.Blocked + 1, atInterception.Blocked);

        var released = await client.PostAsJsonAsync(
            $"/api/chat/approvals/{call.GetProperty("id").GetGuid()}/approve", new { });
        released.EnsureSuccessStatusCode();
        var outcome = await released.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Executed", outcome.GetProperty("status").GetString());

        // The release really did run the tool — and the tool refused, because the Mock provider set
        // invoiceNumber to the entire sentence rather than to the number inside it. Asserted rather
        // than glossed over: it is why the success path is unproven here, and it goes red the day a
        // provider fills the argument properly, which is the cue to strengthen this test.
        Assert.Contains("No invoice numbered", outcome.GetProperty("result").GetString()!);

        // A refused approval leaves the bill exactly where it was. The domain record of WHO
        // approved an invoice — the half of AC3 a bar audit actually reads — is the invoice's own
        // ApprovedByUserId/ApprovedAt, proven on the success path by the separation-of-duties tests
        // above; this test owns the platform half.
        var invoice = await db.Invoices.Where(i => i.Number == number).SingleAsync();
        await db.Entry(invoice).ReloadAsync();
        Assert.Equal(InvoiceStatus.Draft, invoice.Status);
        Assert.Null(invoice.ApprovedByUserId);

        // The release adds NOTHING to the tool-call audit, and that is the platform's design rather
        // than a hole. A gated call is recorded on the approvals queue — which carries who released
        // it and what came back — while the tool-call audit carries ungated executions; the
        // disclosure feed below is the union of the two, and it filters the blocked row out so a
        // single decision is not counted twice. Polled for 3s to make this a settled fact and not a
        // race. Asserted so the day releases DO start landing in the tool-call audit, this goes red
        // and whoever changes it has to decide what the disclosure feed should now show.
        for (var i = 0; i < 10; i++)
        {
            if ((await ApprovalAuditAsync(client)).Succeeded > before.Succeeded) break;
            await Task.Delay(300);
        }
        var afterRelease = await ApprovalAuditAsync(client);
        Assert.Equal(before.Succeeded, afterRelease.Succeeded);

        // Append-only holds for what IS recorded: the block was not rewritten or retracted.
        Assert.Equal(atInterception.Blocked, afterRelease.Blocked);

        // Where AC3's "audit-logged" is discharged for a RELEASED approval is therefore the domain
        // record — Invoice.ApprovedByUserId/ApprovedAt, asserted on the success path by
        // A_different_user_can_approve_and_the_approval_is_recorded_then_sendable — plus the gate
        // row above. Platform main also unions both into a client-facing feed at
        // /api/platform/ai-decisions, but this product's pinned Cortex 0.1.0-alpha.14 answers 404
        // for it (asserted, so it stops being a 404 the moment issue #40's upgrade lands and
        // somebody has to come back and assert the feed instead).
        var decisions = await client.GetAsync("/api/platform/ai-decisions");
        Assert.Equal(HttpStatusCode.NotFound, decisions.StatusCode);
    }

    /// <summary>
    /// The <c>approve_invoice</c> calls the append-only audit holds, split by outcome. Counted
    /// rather than asserted-absent because the whole collection shares one database, so an earlier
    /// test's approval is a legitimate row — it is the <i>delta</i> across each step that is the
    /// claim. Split by outcome because the two move independently: the gate writes a blocked row at
    /// interception, and — see TODO(plenipo#140) — writes nothing at all when the call is released.
    /// </summary>
    private static async Task<(int Succeeded, int Blocked)> ApprovalAuditAsync(HttpClient client)
    {
        var audit = await client.GetFromJsonAsync<JsonElement>("/api/admin/audit/tool-calls");
        var calls = audit.EnumerateArray().Where(a =>
            a.GetProperty("toolName").GetString() == "approve_invoice" &&
            a.GetProperty("permission").GetString() == "tools.legal.approve_invoice").ToList();

        return (calls.Count(a => a.GetProperty("success").GetBoolean()),
                calls.Count(a => !a.GetProperty("success").GetBoolean()));
    }

    private async Task<Guid> SeedMatterAsync(string name)
    {
        using var client = fixture.AdminClient();
        var response = await client.PostAsJsonAsync("/api/legal/matters", new
        {
            name,
            clientName = name.Split(" - ")[0],
            practiceArea = "Corporate",
        });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetGuid();
    }
}
