using Cortex.Modules.Legal.Persistence;

namespace Cortex.Modules.Legal.Tests;

/// <summary>
/// The pure core of trust accounting: amount bounds, the client-ledger balance, the fail-closed
/// disbursement rule (to zero, never below), three-way agreement to the cent, and the
/// reconciliation worksheet — legs, per-matter ledgers, discrepancy guidance, and the critical
/// flag on a negative ledger.
/// </summary>
public sealed class TrustAccountingTests
{
    [Theory]
    [InlineData(0.01, true)]
    [InlineData(5000, true)]
    [InlineData(1_000_000_000, true)]
    [InlineData(0, false)]
    [InlineData(-50, false)]
    [InlineData(1_000_000_001, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void AmountIsValid_PositiveAndUnderABillion(double amount, bool valid)
    {
        Assert.Equal(valid, TrustAccounting.AmountIsValid(amount));
    }

    [Fact]
    public void Balance_IsDepositsMinusDisbursements()
    {
        var balance = TrustAccounting.Balance(
        [
            (TrustTransactionType.Deposit, 5000m),
            (TrustTransactionType.Disbursement, 1200.50m),
            (TrustTransactionType.Deposit, 300m),
        ]);

        Assert.Equal(4099.50m, balance);
        Assert.Equal(0m, TrustAccounting.Balance(Array.Empty<(TrustTransactionType, decimal)>()));
    }

    [Theory]
    [InlineData(5000, 4999.99, true)]
    [InlineData(5000, 5000, true)] // to exactly zero is lawful
    [InlineData(5000, 5000.01, false)] // one cent below zero is misappropriation
    [InlineData(0, 0.01, false)] // an empty ledger disburses nothing
    public void CanDisburse_ToZeroNeverBelow(double balance, double amount, bool allowed)
    {
        Assert.Equal(allowed, TrustAccounting.CanDisburse((decimal)balance, (decimal)amount));
    }

    [Fact]
    public void Reconciles_RequiresAllThreeLegsToAgreeToTheCent()
    {
        Assert.True(TrustAccounting.Reconciles(4099.50m, 4099.50m, 4099.50m));
        Assert.False(TrustAccounting.Reconciles(4100m, 4099.50m, 4099.50m));
        Assert.False(TrustAccounting.Reconciles(4099.50m, 4099.50m, 4099.49m));
    }

    [Fact]
    public void Money_FormatsInvariantWithThousandsAndSignedNegatives()
    {
        Assert.Equal("$1,234.50", TrustAccounting.Money(1234.5m));
        Assert.Equal("$0.00", TrustAccounting.Money(0m));
        Assert.Equal("-$25.00", TrustAccounting.Money(-25m));
    }

    [Fact]
    public void RestrictedLabel_ShowsDocketNumberNeverTheName()
    {
        Assert.Equal("[restricted matter 2026-0007]", TrustAccounting.RestrictedLabel("2026-0007"));
        Assert.Equal("[restricted matter]", TrustAccounting.RestrictedLabel(null));
    }

    [Fact]
    public void ComposeReconciliation_CarriesThreeLegsLedgersAndTheReconciledVerdict()
    {
        var body = TrustAccounting.ComposeReconciliation(
            bankBalance: 4099.50m,
            bookBalance: 4099.50m,
            ledgers: [("Acme / Initech NDA", 4000m), ("Meridian estate", 99.50m)],
            asOf: new DateOnly(2026, 7, 31),
            generatedOn: new DateOnly(2026, 8, 1));

        Assert.Contains("as of 2026-07-31", body);
        Assert.Contains("Bank statement balance", body);
        Assert.Contains("Trust book balance", body);
        Assert.Contains("Sum of client ledgers", body);
        Assert.Contains("Acme / Initech NDA: $4,000.00", body);
        Assert.Contains("Meridian estate: $99.50", body);
        Assert.Contains("RECONCILED", body);
        Assert.DoesNotContain("DISCREPANCY", body);
        Assert.Contains("5-7 years", body);
    }

    [Fact]
    public void ComposeReconciliation_FlagsABankDiscrepancyWithDirectionAndGuidance()
    {
        var body = TrustAccounting.ComposeReconciliation(
            bankBalance: 4200m,
            bookBalance: 4099.50m,
            ledgers: [("Acme", 4099.50m)],
            asOf: new DateOnly(2026, 7, 31),
            generatedOn: new DateOnly(2026, 8, 1));

        Assert.Contains("DISCREPANCY", body);
        Assert.DoesNotContain("RECONCILED —", body);
        Assert.Contains("bank is $100.50 higher", body);
        Assert.Contains("deposits in transit", body);
        Assert.Contains("Do not sign until resolved", body);
    }

    [Fact]
    public void ComposeReconciliation_FlagsANegativeClientLedgerAsCritical()
    {
        var body = TrustAccounting.ComposeReconciliation(
            bankBalance: 100m,
            bookBalance: 100m,
            ledgers: [("Acme", 150m), ("Meridian", -50m)],
            asOf: new DateOnly(2026, 7, 31),
            generatedOn: new DateOnly(2026, 8, 1));

        Assert.Contains("Meridian: -$50.00   *** CRITICAL: negative client ledger", body);
        Assert.DoesNotContain("Acme: $150.00   ***", body);
    }

    [Fact]
    public void ComposeReconciliation_TreatsBookVsLedgerDriftAsDataCorruption()
    {
        var body = TrustAccounting.ComposeReconciliation(
            bankBalance: 100m,
            bookBalance: 100m,
            ledgers: [("Acme", 90m)],
            asOf: new DateOnly(2026, 7, 31),
            generatedOn: new DateOnly(2026, 8, 1));

        Assert.Contains("DISCREPANCY", body);
        Assert.Contains("data corruption", body);
    }
}
