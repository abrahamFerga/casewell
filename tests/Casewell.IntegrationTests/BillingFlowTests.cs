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
