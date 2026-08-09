using Cortex.Modules.Legal.Persistence;

namespace Cortex.Modules.Legal.Tests;

/// <summary>
/// The pure core of billing: the separation-of-duties rule, six-minute rounding, per-year invoice
/// numbering, activity-code suggestion, and the one-click draft's line composition and arithmetic.
/// </summary>
public sealed class BillingTests
{
    // ── Separation of duties: the rule the whole approval workflow exists to enforce ──

    [Fact]
    public void CanApprove_RefusesThePreparer()
    {
        var preparer = Guid.NewGuid();

        Assert.False(Billing.CanApprove(preparer, preparer));
    }

    [Fact]
    public void CanApprove_AllowsAnyoneElse()
    {
        Assert.True(Billing.CanApprove(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void CanApprove_RefusesWhenEitherPartyIsUnknown()
    {
        // A bill with no attributable preparer must not slip through on a null — a permissive
        // default here would quietly disable the control.
        Assert.False(Billing.CanApprove(null, Guid.NewGuid()));
        Assert.False(Billing.CanApprove(Guid.NewGuid(), null));
        Assert.False(Billing.CanApprove(null, null));
    }

    // ── Six-minute increments ──

    [Theory]
    [InlineData(0, 0.1)]          // a timer stopped immediately still bills the minimum
    [InlineData(1, 0.1)]
    [InlineData(6, 0.1)]          // exactly one increment
    [InlineData(7, 0.2)]          // any started increment bills whole
    [InlineData(60, 1.0)]
    [InlineData(84, 1.4)]
    [InlineData(85, 1.5)]
    public void ElapsedToHours_RoundsUpToTheNextTenthOfAnHour(int minutes, double expected)
    {
        Assert.Equal((decimal)expected, Billing.ElapsedToHours(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void Elapsed_FloorsAtZeroAgainstClockSkew()
    {
        Assert.Equal(TimeSpan.Zero, Billing.Elapsed(DateTimeOffset.UtcNow.AddMinutes(5)));
    }

    [Fact]
    public void ElapsedLabel_ReadsAsHoursAndMinutes()
    {
        Assert.Equal("1h 24m", Billing.ElapsedLabel(TimeSpan.FromMinutes(84)));
        Assert.Equal("12m", Billing.ElapsedLabel(TimeSpan.FromMinutes(12)));
        Assert.Equal("1m", Billing.ElapsedLabel(TimeSpan.FromSeconds(5)));
    }

    // ── Validation: typo protection ──

    [Theory]
    [InlineData(0.01, true)]
    [InlineData(402, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(1_000_000_001, false)]
    public void AmountIsValid_RejectsNonPositiveAndAbsurd(double amount, bool valid)
    {
        Assert.Equal(valid, Billing.AmountIsValid(amount));
    }

    [Theory]
    [InlineData(350, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(100_001, false)]
    public void RateIsValid_BoundsTheHourlyRate(double rate, bool valid)
    {
        Assert.Equal(valid, Billing.RateIsValid(rate));
    }

    // ── Invoice numbering ──

    [Fact]
    public void NextInvoiceNumber_StartsAtOneForAFreshYear()
    {
        Assert.Equal("INV-2026-0001", Billing.NextInvoiceNumber(2026, []));
    }

    [Fact]
    public void NextInvoiceNumber_ContinuesFromTheHighestInThatYear()
    {
        string[] existing = ["INV-2026-0001", "INV-2026-0009", "INV-2025-0044"];

        // Highest wins, not the count — a voided or deleted number must never be reused.
        Assert.Equal("INV-2026-0010", Billing.NextInvoiceNumber(2026, existing));
    }

    [Fact]
    public void NextInvoiceNumber_KeepsYearsInSeparateSequences()
    {
        Assert.Equal("INV-2027-0001", Billing.NextInvoiceNumber(2027, ["INV-2026-0031"]));
    }

    // ── Activity codes ──

    [Fact]
    public void SuggestActivityCode_PrefersTheMostSpecificKeyword()
    {
        // "call with client" (A106) must beat the bare "call" (A108) it contains.
        Assert.Equal("A106", Billing.SuggestActivityCode("Call with client about the NDA"));
    }

    [Fact]
    public void SuggestActivityCode_FindsDraftingAndDepositions()
    {
        Assert.Equal("A103", Billing.SuggestActivityCode("Drafted the NDA"));
        Assert.Equal("L330", Billing.SuggestActivityCode("Deposition of the CFO"));
    }

    [Fact]
    public void SuggestActivityCode_ReturnsNullRatherThanGuessing()
    {
        // A wrong code is worse than none — it is what a client challenges.
        Assert.Null(Billing.SuggestActivityCode("qqq zzz"));
        Assert.Null(Billing.SuggestActivityCode(null));
        Assert.Null(Billing.SuggestActivityCode("   "));
    }

    [Fact]
    public void NormalizeActivityCode_AcceptsKnownCodesCaseInsensitivelyAndRejectsInvented()
    {
        Assert.Equal("L110", Billing.NormalizeActivityCode("l110"));
        Assert.Null(Billing.NormalizeActivityCode("Z999"));
        Assert.Null(Billing.NormalizeActivityCode(null));
    }

    // ── The one-click draft ──

    [Fact]
    public void BuildLines_CombinesTimeExpensesAndTheFlatFee()
    {
        var time = new List<TimeEntry>
        {
            new() { Description = "Drafted NDA", Hours = 2m, WorkedOn = new DateOnly(2026, 7, 1), ActivityCode = "A103" },
        };
        var expenses = new List<Expense>
        {
            new() { Description = "Filing fee", Amount = 402m, IncurredOn = new DateOnly(2026, 7, 2) },
        };

        var lines = Billing.BuildLines(time, expenses, 2500m, "Fixed fee for the formation", 350m);

        Assert.Equal(3, lines.Count);
        Assert.Equal(InvoiceLineKind.Time, lines[0].Kind);
        Assert.Equal(700m, lines[0].Amount);                       // 2h × 350
        Assert.Equal("[A103] Drafted NDA", lines[0].Description);   // the code rides the narrative
        Assert.Equal(InvoiceLineKind.Expense, lines[1].Kind);
        Assert.Equal(402m, lines[1].Amount);
        Assert.Equal(InvoiceLineKind.FlatFee, lines[2].Kind);
        Assert.Equal(2500m, lines[2].Amount);
        Assert.Equal("Fixed fee for the formation", lines[2].Description);
        Assert.Equal(3602m, lines.Sum(l => l.Amount));
    }

    [Fact]
    public void BuildLines_PrefersAnEntrysOwnAgreedRate()
    {
        var time = new List<TimeEntry>
        {
            new() { Description = "At the agreed rate", Hours = 1m, WorkedOn = new DateOnly(2026, 7, 1), Rate = 200m },
            new() { Description = "At the invoice rate", Hours = 1m, WorkedOn = new DateOnly(2026, 7, 1) },
        };

        var lines = Billing.BuildLines(time, [], null, null, 350m);

        Assert.Equal(200m, lines[0].Amount);
        Assert.Equal(350m, lines[1].Amount);
    }

    [Fact]
    public void BuildLines_RoundsEachLineToTheCentSoTheTotalAddsUp()
    {
        var time = new List<TimeEntry>
        {
            new() { Description = "Odd fraction", Hours = 0.3m, WorkedOn = new DateOnly(2026, 7, 1) },
        };

        var lines = Billing.BuildLines(time, [], null, null, 333.33m);

        // 0.3 × 333.33 = 99.999 → 100.00, not a trailing fraction the client cannot reconcile.
        Assert.Equal(100.00m, lines[0].Amount);
    }

    [Fact]
    public void BuildLines_IsEmptyWhenThereIsNothingUnbilled()
    {
        Assert.Empty(Billing.BuildLines([], [], null, null, 350m));
    }

    [Fact]
    public void BuildLines_CarriesTheSourceIdBackToTheOriginatingRow()
    {
        var entry = new TimeEntry { Id = Guid.NewGuid(), Description = "Work", Hours = 1m, WorkedOn = new DateOnly(2026, 7, 1) };
        var expense = new Expense { Id = Guid.NewGuid(), Description = "Courier", Amount = 25m, IncurredOn = new DateOnly(2026, 7, 1) };

        var lines = Billing.BuildLines([entry], [expense], null, null, 350m);

        Assert.Equal(entry.Id, lines[0].SourceId);
        Assert.Equal(expense.Id, lines[1].SourceId);
    }

    [Fact]
    public void PeriodLabel_ReadsInceptionForAnOpenStart()
    {
        Assert.Equal("inception – 2026-07-31", Billing.PeriodLabel(null, new DateOnly(2026, 7, 31)));
        Assert.Equal("2026-07-01 – 2026-07-31", Billing.PeriodLabel(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31)));
    }
}
