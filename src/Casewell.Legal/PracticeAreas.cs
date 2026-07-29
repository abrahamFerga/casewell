namespace Cortex.Modules.Legal;

/// <summary>
/// The curated practice-area taxonomy matters categorize under. A fixed vocabulary (rather than
/// free text) is what makes the Matters tab filterable and the reporting epic's per-area rollups
/// meaningful; <see cref="Normalize"/> is forgiving on input so the agent can pass what the user
/// said ("ip", "employment law") and still land on a canonical value.
/// </summary>
public static class PracticeAreas
{
    public static readonly IReadOnlyList<string> All =
    [
        "Corporate",
        "Litigation",
        "Employment",
        "Real Estate",
        "Intellectual Property",
        "Family",
        "Criminal",
        "Immigration",
        "Tax",
        "Estate Planning",
    ];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ip"] = "Intellectual Property",
        ["m&a"] = "Corporate",
        ["mergers and acquisitions"] = "Corporate",
        ["labor"] = "Employment",
        ["labour"] = "Employment",
        ["property"] = "Real Estate",
        ["wills"] = "Estate Planning",
        ["trusts"] = "Estate Planning",
        ["probate"] = "Estate Planning",
    };

    /// <summary>
    /// Maps free input to a canonical area: exact (case-insensitive), then alias, then a
    /// single-word containment pass ("employment law" → Employment). Null when nothing matches —
    /// the caller should list <see cref="All"/> rather than store an unknown value.
    /// </summary>
    public static string? Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var trimmed = input.Trim();
        var exact = All.FirstOrDefault(a => string.Equals(a, trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        if (Aliases.TryGetValue(trimmed, out var aliased))
        {
            return aliased;
        }

        return All.FirstOrDefault(a =>
            trimmed.Contains(a, StringComparison.OrdinalIgnoreCase) ||
            a.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public static string Listed => string.Join(", ", All);
}
