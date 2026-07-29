using Cortex.Modules.Legal.Persistence;

namespace Cortex.Modules.Legal.Tests;

/// <summary>
/// The pure core of time capture and pre-billing: hour bounds (typo protection), period
/// labeling, and the pre-bill body — narrative lines plus billable/non-billable totals.
/// </summary>
public sealed class TimeCaptureTests
{
    [Theory]
    [InlineData(0.1, true)]
    [InlineData(24, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(24.5, false)]
    public void HoursAreValid_BoundsToAWorkday(double hours, bool valid)
    {
        Assert.Equal(valid, TimeCapture.HoursAreValid(hours));
    }

    [Fact]
    public void PeriodLabel_ReadsInceptionForAnOpenStart()
    {
        Assert.Equal("inception – 2026-07-31", TimeCapture.PeriodLabel(DateOnly.MinValue, new DateOnly(2026, 7, 31)));
        Assert.Equal("2026-07-01 – 2026-07-31", TimeCapture.PeriodLabel(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31)));
    }

    [Fact]
    public void ComposePrebill_CarriesNarrativesTotalsAndTheNotAnInvoiceFraming()
    {
        var entries = new List<TimeEntry>
        {
            new() { Description = "Drafted NDA", Hours = 1.5m, WorkedOn = new DateOnly(2026, 7, 1), Billable = true, UserDisplay = "Ada" },
            new() { Description = "Internal research", Hours = 0.5m, WorkedOn = new DateOnly(2026, 7, 2), Billable = false },
        };

        var body = TimeCapture.ComposePrebill("Acme / Initech NDA", "Acme LLC", "2026-07-01 – 2026-07-31", entries);

        Assert.Contains("Matter: Acme / Initech NDA   Client: Acme LLC", body);
        Assert.Contains("Drafted NDA", body);
        Assert.Contains("— Ada", body);
        Assert.Contains("[non-billable]", body);
        Assert.Contains("Billable: 1.5h   Non-billable: 0.5h   Total: 2h", body);
        Assert.Contains("not an invoice", body);
    }

    [Fact]
    public void ComposePrebill_OmitsTheClientLineWhenUnknown()
    {
        var body = TimeCapture.ComposePrebill(
            "Acme", null, "inception – 2026-07-31",
            [new TimeEntry { Description = "Call", Hours = 1m, WorkedOn = new DateOnly(2026, 7, 3) }]);

        Assert.Contains("Matter: Acme\n", body.Replace("\r\n", "\n"));
        Assert.DoesNotContain("Client:", body);
    }
}
