using System.ComponentModel;
using System.Globalization;
using System.Text;
using Cortex.Application.Documents;
using Cortex.Application.Files;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Cortex.Modules.Legal.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Modules.Legal;

/// <summary>
/// Trust accounting (IOLTA) — the CosmoLex gap from the 2026 research refresh. Client funds are
/// sacred: every write is approval-gated and append-only (corrections are contra entries), and
/// the one rule that matters is fail-closed — a disbursement that would overdraw a matter's
/// ledger is refused, not warned, because a negative client ledger is misappropriation even
/// when the pooled account is positive (Model Rule 1.15). The reconciliation export is the
/// monthly three-way worksheet most bars mandate: the user supplies the bank-statement figure;
/// the module supplies the book balance and the client-ledger sum, and files the evidence.
/// </summary>
public sealed class TrustTools(
    LegalDbContext db,
    IFileStore files,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IPdfRenderer pdfRenderer)
{
    [Description("Record a DEPOSIT of client funds into the trust account for a matter (retainer, settlement proceeds, filing-fee advance). Append-only compliance record; requires human approval.")]
    public async Task<string> RecordTrustDeposit(
        [Description("The matter the funds belong to.")] string matterName,
        [Description("Amount deposited, e.g. 5000 or 1250.50. Always positive.")] double amount,
        [Description("What the money is — 'retainer received', 'settlement proceeds', 'cost advance'.")] string description,
        [Description("Optional bank reference: check number, wire confirmation, receipt id.")] string? reference = null,
        [Description("The banking date as an ISO date (default: today).")] string? date = null,
        CancellationToken cancellationToken = default)
    {
        return await RecordAsync(TrustTransactionType.Deposit, matterName, amount, description, reference, date, cancellationToken);
    }

    [Description("Record a DISBURSEMENT of client funds from the trust account for a matter (fee transfer after invoicing, filing fee paid, refund to client). FAIL-CLOSED: refused outright if it would overdraw the matter's trust balance — a negative client ledger is misappropriation. Requires human approval.")]
    public async Task<string> RecordTrustDisbursement(
        [Description("The matter the funds leave.")] string matterName,
        [Description("Amount disbursed, e.g. 750 or 320.25. Always positive.")] double amount,
        [Description("What the payment is — 'earned fees per invoice 2026-014', 'court filing fee', 'balance returned to client'.")] string description,
        [Description("Optional bank reference: check number, wire confirmation.")] string? reference = null,
        [Description("The banking date as an ISO date (default: today).")] string? date = null,
        CancellationToken cancellationToken = default)
    {
        return await RecordAsync(TrustTransactionType.Disbursement, matterName, amount, description, reference, date, cancellationToken);
    }

    [Description("A matter's trust balance, or (with no matter) every matter ledger plus the firm's trust book balance.")]
    public async Task<string> TrustBalance(
        [Description("Optional matter name; omit for the firm-wide view.")] string? matterName = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(matterName))
        {
            var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
            if (matter is null)
            {
                return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
            }

            var entries = await db.TrustTransactions
                .Where(t => t.MatterId == matter.Id)
                .ToListAsync(cancellationToken);
            if (entries.Count == 0)
            {
                return $"Matter '{matter.Name}' holds no client funds — no trust activity recorded.";
            }

            var balance = TrustAccounting.Balance(entries);
            var last = entries.Max(t => t.OccurredOn);
            return $"Trust balance on matter '{matter.Name}': {TrustAccounting.Money(balance)} " +
                   $"({entries.Count} transaction(s), last activity {last:yyyy-MM-dd}). " +
                   "Full ledger: list_trust_transactions.";
        }

        // Firm-wide: the caller sees per-matter lines for matters inside their walls; walled
        // balances still count toward the book total (the account must sum), name withheld —
        // the check_conflicts convention.
        var all = await db.TrustTransactions
            .Join(db.Matters, t => t.MatterId, m => m.Id,
                (t, m) => new { t.Type, t.Amount, m.Name, m.MatterNumber, m.RestrictedUserIdsJson })
            .ToListAsync(cancellationToken);
        if (all.Count == 0)
        {
            return "No trust activity recorded yet. Record client funds with record_trust_deposit.";
        }

        var byMatter = all
            .GroupBy(t => new { t.Name, t.MatterNumber, t.RestrictedUserIdsJson })
            .Select(g => new
            {
                Label = Matter.WallAllows(g.Key.RestrictedUserIdsJson, currentUser.UserId)
                    ? g.Key.Name
                    : TrustAccounting.RestrictedLabel(g.Key.MatterNumber),
                Balance = TrustAccounting.Balance(g.Select(x => (x.Type, x.Amount))),
            })
            .OrderByDescending(m => m.Balance);

        var sb = new StringBuilder("Trust ledgers by matter:\n");
        foreach (var m in byMatter)
        {
            sb.AppendLine($"- {m.Label}: {TrustAccounting.Money(m.Balance)}");
        }

        sb.Append($"Firm trust book balance: {TrustAccounting.Money(TrustAccounting.Balance(all.Select(t => (t.Type, t.Amount))))}.");
        return sb.ToString();
    }

    [Description("A matter's full trust ledger, oldest first with a running balance — the client ledger a bar audit asks for.")]
    public async Task<string> ListTrustTransactions(
        [Description("The matter name.")] string matterName,
        CancellationToken cancellationToken = default)
    {
        var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        var entries = await db.TrustTransactions
            .Where(t => t.MatterId == matter.Id)
            .OrderBy(t => t.OccurredOn).ThenBy(t => t.CreatedAt)
            .ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            return $"Matter '{matter.Name}' has no trust activity. Record client funds with record_trust_deposit.";
        }

        // A long ledger lists its most recent page the way a paper one would: earlier activity
        // collapses into a brought-forward balance, so the final balance is always the true one.
        var sb = new StringBuilder($"Trust ledger — matter '{matter.Name}':\n");
        var running = 0m;
        if (entries.Count > 200)
        {
            var earlier = entries[..^200];
            running = TrustAccounting.Balance(earlier);
            sb.AppendLine($"- ({earlier.Count} earlier entr(ies) — balance brought forward: {TrustAccounting.Money(running)})");
            entries = entries[^200..];
        }

        foreach (var e in entries)
        {
            running += e.Type == TrustTransactionType.Deposit ? e.Amount : -e.Amount;
            sb.AppendLine($"- {e.OccurredOn:yyyy-MM-dd} · {(e.Type == TrustTransactionType.Deposit ? "deposit" : "disbursement"),-12} · " +
                          $"{TrustAccounting.Money(e.Amount),12} · balance {TrustAccounting.Money(running)} · {e.Description}" +
                          $"{(e.Reference is null ? "" : $" [{e.Reference}]")}{(e.UserDisplay is null ? "" : $" — {e.UserDisplay}")}");
        }

        sb.Append($"Balance: {TrustAccounting.Money(running)}.");
        return sb.ToString();
    }

    [Description("Generate the monthly THREE-WAY trust reconciliation (bank statement vs. book balance vs. sum of client ledgers) as a PDF worksheet filed in the document store — the evidence most bars require, retained 5-7 years. You supply the bank-statement figure; the module supplies the other two legs. Side-effecting: writes a document and requires human approval.")]
    public async Task<string> ExportTrustReconciliation(
        [Description("The trust account's ending balance from the BANK STATEMENT, e.g. 12500.75.")] double bankStatementBalance,
        [Description("Reconcile as of this ISO date (inclusive; default: today). Use the bank statement's closing date.")] string? asOfDate = null,
        CancellationToken cancellationToken = default)
    {
        if (double.IsNaN(bankStatementBalance) || double.IsInfinity(bankStatementBalance) || bankStatementBalance < 0)
        {
            return "The bank-statement balance must be zero or positive - read it off the statement's closing line.";
        }

        var asOf = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(asOfDate) && !DateOnly.TryParse(asOfDate, CultureInfo.InvariantCulture, out asOf))
        {
            return $"'{asOfDate}' is not a date I can parse — use an ISO date like 2026-07-31, or omit it for today.";
        }

        // Every matter's funds count — walls hide names, never money. A reconciliation that
        // skipped restricted matters would be arithmetic fiction.
        var all = await db.TrustTransactions
            .Where(t => t.OccurredOn <= asOf)
            .Join(db.Matters, t => t.MatterId, m => m.Id,
                (t, m) => new { t.Type, t.Amount, m.Name, m.MatterNumber, m.RestrictedUserIdsJson })
            .ToListAsync(cancellationToken);
        if (all.Count == 0)
        {
            return $"No trust transactions on or before {asOf:yyyy-MM-dd} — nothing to reconcile.";
        }

        var ledgers = all
            .GroupBy(t => new { t.Name, t.MatterNumber, t.RestrictedUserIdsJson })
            .Select(g => (
                MatterLabel: Matter.WallAllows(g.Key.RestrictedUserIdsJson, currentUser.UserId)
                    ? g.Key.Name
                    : TrustAccounting.RestrictedLabel(g.Key.MatterNumber),
                Balance: TrustAccounting.Balance(g.Select(x => (x.Type, x.Amount)))))
            .OrderBy(l => l.MatterLabel, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var book = TrustAccounting.Balance(all.Select(t => (t.Type, t.Amount)));

        var body = TrustAccounting.ComposeReconciliation(
            (decimal)bankStatementBalance, book, ledgers, asOf, DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime));

        var pdf = pdfRenderer.Render($"Trust reconciliation — as of {asOf:yyyy-MM-dd}", body);
        using var stream = new MemoryStream(pdf);
        var stored = await files.SaveAsync(
            $"trust-reconciliation-{asOf:yyyyMMdd}.pdf", "application/pdf", stream,
            source: "trust-reconciliation", cancellationToken);

        var reconciled = TrustAccounting.Reconciles((decimal)bankStatementBalance, book, ledgers.Sum(l => l.Balance));
        return $"Filed three-way reconciliation '{stored.FileName}' (file id: {stored.Id}) as of {asOf:yyyy-MM-dd}: " +
               $"bank {TrustAccounting.Money((decimal)bankStatementBalance)}, book {TrustAccounting.Money(book)}, " +
               $"client ledgers {TrustAccounting.Money(ledgers.Sum(l => l.Balance))} — " +
               (reconciled
                   ? "RECONCILED, all three legs agree."
                   : "DISCREPANCY — the legs do not agree; resolve it before signing the worksheet.");
    }

    private async Task<string> RecordAsync(
        TrustTransactionType type, string matterName, double amount, string description,
        string? reference, string? date, CancellationToken cancellationToken)
    {
        var matter = await FindAccessibleMatterAsync(matterName, cancellationToken);
        if (matter is null)
        {
            return $"No matter named '{matterName}' exists. Use list_matters to find the right name.";
        }

        if (!TrustAccounting.AmountIsValid(amount))
        {
            return "amount must be greater than 0 (the direction comes from deposit vs. disbursement, never a sign).";
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return "A trust transaction needs a description — it is the ledger line a bar audit reads.";
        }

        var occurredOn = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);
        if (!string.IsNullOrWhiteSpace(date) && !DateOnly.TryParse(date, CultureInfo.InvariantCulture, out occurredOn))
        {
            return $"'{date}' is not a date I can parse — use an ISO date like 2026-07-15, or omit it for today.";
        }

        var value = (decimal)amount;
        if (type == TrustTransactionType.Disbursement)
        {
            var entries = await db.TrustTransactions
                .Where(t => t.MatterId == matter.Id)
                .ToListAsync(cancellationToken);
            var balance = TrustAccounting.Balance(entries);
            if (!TrustAccounting.CanDisburse(balance, value))
            {
                return $"REFUSED: disbursing {TrustAccounting.Money(value)} would overdraw matter " +
                       $"'{matter.Name}''s trust balance of {TrustAccounting.Money(balance)}. A negative client " +
                       "ledger is misappropriation even when the pooled account is positive (Model Rule 1.15). " +
                       "Deposit funds first, or disburse at most the balance.";
            }
        }

        db.TrustTransactions.Add(new TrustTransaction
        {
            TenantId = tenant.RequireTenantId(),
            MatterId = matter.Id,
            Type = type,
            Amount = value,
            Description = description.Trim(),
            Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
            OccurredOn = occurredOn,
            UserId = currentUser.UserId,
            UserDisplay = currentUser.DisplayName,
        });
        await db.SaveChangesAsync(cancellationToken);

        var after = TrustAccounting.Balance(await db.TrustTransactions
            .Where(t => t.MatterId == matter.Id)
            .ToListAsync(cancellationToken));
        var verb = type == TrustTransactionType.Deposit ? "deposit into" : "disbursement from";
        return $"Recorded trust {verb} matter '{matter.Name}': {TrustAccounting.Money(value)} on {occurredOn:yyyy-MM-dd}" +
               $"{(reference is null ? "" : $" [{reference.Trim()}]")} — {description.Trim()}. " +
               $"Matter trust balance: {TrustAccounting.Money(after)}.";
    }

    private async Task<Matter?> FindAccessibleMatterAsync(string name, CancellationToken cancellationToken)
    {
        var normalized = name.Trim();
        var matter = await db.Matters.FirstOrDefaultAsync(
            m => EF.Functions.ILike(m.Name, normalized), cancellationToken);
        return matter is not null && matter.IsAccessibleTo(currentUser.UserId) ? matter : null;
    }
}

/// <summary>Pure trust-accounting rules behind the tools, unit-tested without a database.</summary>
public static class TrustAccounting
{
    /// <summary>Positive and under a billion — beyond that it's a typo, not a client's money.</summary>
    public static bool AmountIsValid(double amount) =>
        !double.IsNaN(amount) && !double.IsInfinity(amount) && amount is > 0 and <= 1_000_000_000;

    /// <summary>Deposits minus disbursements — the client-ledger balance.</summary>
    public static decimal Balance(IEnumerable<(TrustTransactionType Type, decimal Amount)> entries) =>
        entries.Sum(e => e.Type == TrustTransactionType.Deposit ? e.Amount : -e.Amount);

    /// <summary>Overload for entity lists (tools read straight from the DbSet).</summary>
    public static decimal Balance(IEnumerable<TrustTransaction> entries) =>
        Balance(entries.Select(e => (e.Type, e.Amount)));

    /// <summary>
    /// The fail-closed rule: a disbursement may take the ledger to exactly zero, never below.
    /// </summary>
    public static bool CanDisburse(decimal balance, decimal amount) => amount <= balance;

    /// <summary>Bank = book = sum of client ledgers, to the cent.</summary>
    public static bool Reconciles(decimal bank, decimal book, decimal ledgerSum) =>
        bank == book && book == ledgerSum;

    /// <summary>"$1,234.50" (invariant), "-$25.00" for negatives — IOLTA is a US construct.</summary>
    public static string Money(decimal amount) =>
        amount < 0
            ? "-$" + (-amount).ToString("#,0.00", CultureInfo.InvariantCulture)
            : "$" + amount.ToString("#,0.00", CultureInfo.InvariantCulture);

    /// <summary>A walled matter shows its docket number, never its name — money counts, names stay hidden.</summary>
    public static string RestrictedLabel(string? matterNumber) =>
        matterNumber is null ? "[restricted matter]" : $"[restricted matter {matterNumber}]";

    /// <summary>
    /// The three-way worksheet body. Legs two and three both derive from the same append-only
    /// ledger, so inside Casewell they agree by construction — the leg that can genuinely
    /// disagree is the bank's, and the worksheet says so. Any negative client ledger is flagged
    /// as critical even though the disbursement guard should make one impossible.
    /// </summary>
    public static string ComposeReconciliation(
        decimal bankBalance,
        decimal bookBalance,
        IReadOnlyList<(string MatterLabel, decimal Balance)> ledgers,
        DateOnly asOf,
        DateOnly generatedOn)
    {
        var ledgerSum = ledgers.Sum(l => l.Balance);

        var body = new StringBuilder();
        body.AppendLine($"Three-way trust account reconciliation — as of {asOf:yyyy-MM-dd}");
        body.AppendLine($"Generated: {generatedOn:yyyy-MM-dd}");
        body.AppendLine();
        body.AppendLine($"Leg 1 — Bank statement balance:      {Money(bankBalance),15}");
        body.AppendLine($"Leg 2 — Trust book balance:          {Money(bookBalance),15}");
        body.AppendLine($"Leg 3 — Sum of client ledgers:       {Money(ledgerSum),15}");
        body.AppendLine();
        body.AppendLine("Client ledgers by matter:");
        foreach (var (label, balance) in ledgers)
        {
            body.AppendLine($"  {label}: {Money(balance)}" +
                            (balance < 0 ? "   *** CRITICAL: negative client ledger — investigate immediately ***" : ""));
        }

        body.AppendLine();
        if (Reconciles(bankBalance, bookBalance, ledgerSum))
        {
            body.AppendLine("Result: RECONCILED — all three legs agree.");
        }
        else
        {
            body.AppendLine("Result: DISCREPANCY — the legs do not agree. Do not sign until resolved.");
            if (bankBalance != bookBalance)
            {
                var diff = bankBalance - bookBalance;
                body.AppendLine($"  Bank vs. book: bank is {Money(Math.Abs(diff))} " +
                                $"{(diff > 0 ? "higher" : "lower")} — look for deposits in transit, " +
                                "outstanding checks, bank fees posted to the wrong account, or unrecorded transactions.");
            }

            if (bookBalance != ledgerSum)
            {
                body.AppendLine("  Book vs. client ledgers: these derive from the same records inside Casewell " +
                                "and should never differ — treat as data corruption and audit the ledger.");
            }
        }

        body.AppendLine();
        body.AppendLine("Book balance and client ledgers computed from Casewell's append-only trust ledger; " +
                        "the bank figure was supplied from the bank statement. Retain this worksheet per your " +
                        "bar's trust-accounting rules (commonly 5-7 years). Not legal or accounting advice.");
        return body.ToString();
    }
}
