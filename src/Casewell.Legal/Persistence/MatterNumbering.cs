using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Modules.Legal.Persistence;

/// <summary>
/// The firm docket-number scheme: <c>YYYY-NNNN</c>, sequence per tenant per opening year. The
/// sequence derives from the tenant's existing numbers rather than a counter table — matter
/// creation is a human-approved, low-frequency act, and the unique index on
/// (TenantId, MatterNumber) turns the rare race into a retry instead of a duplicate.
/// </summary>
public static class MatterNumbering
{
    public static string Format(int year, int sequence) =>
        string.Create(CultureInfo.InvariantCulture, $"{year}-{sequence:0000}");

    /// <summary>The next sequence for <paramref name="year"/> given the tenant's existing numbers (malformed values are ignored).</summary>
    public static int NextSequence(IEnumerable<string?> existingNumbers, int year)
    {
        var prefix = year.ToString(CultureInfo.InvariantCulture) + "-";
        var max = 0;
        foreach (var number in existingNumbers)
        {
            if (number is not null
                && number.StartsWith(prefix, StringComparison.Ordinal)
                && int.TryParse(number.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
                && seq > max)
            {
                max = seq;
            }
        }

        return max + 1;
    }

    /// <summary>
    /// Numbers a just-added matter and saves: the shared creation tail for every path that opens
    /// a matter (chat tool, tab editor, setup wizard). The unique index on (TenantId, MatterNumber)
    /// turns the rare concurrent clash into one retry with a fresh sequence rather than a duplicate.
    /// </summary>
    public static async Task NumberAndSaveAsync(LegalDbContext db, Matter matter, CancellationToken cancellationToken)
    {
        var year = DateTimeOffset.UtcNow.Year;
        for (var attempt = 0; ; attempt++)
        {
            var existingNumbers = await db.Matters
                .Where(m => m.MatterNumber != null && m.Id != matter.Id)
                .Select(m => m.MatterNumber)
                .ToListAsync(cancellationToken);
            matter.MatterNumber = Format(year, NextSequence(existingNumbers, year));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException) when (attempt == 0)
            {
            }
        }
    }
}
