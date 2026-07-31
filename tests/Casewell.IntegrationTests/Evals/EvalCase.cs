using System.Text.Json.Serialization;

namespace Casewell.IntegrationTests.Evals;

/// <summary>
/// One golden-conversation case. Prompt-shaped changes — a tool's <c>[Description]</c>, the
/// module's <c>AgentInstructions</c>, an approval flag — change behaviour without changing code,
/// so they need the same regression net code has.
/// </summary>
/// <remarks>
/// Unknown JSON fields throw rather than being ignored, so a typo in a case file surfaces as a
/// failure instead of silently asserting nothing.
/// </remarks>
public sealed class EvalCase
{
    public required string Name { get; init; }

    /// <summary>The module whose AG-UI endpoint the turn goes to.</summary>
    public required string Module { get; init; }

    /// <summary>The user's turn, phrased the way a real user would.</summary>
    public required string Message { get; init; }

    /// <summary>Dev-auth role the turn runs as. A narrower role is how RBAC gets asserted.</summary>
    public string Role { get; init; } = "system_admin";

    /// <summary>Tools the turn must call.</summary>
    public string[] ExpectToolCalls { get; init; } = [];

    /// <summary>Tools the turn must NOT call — the guard against a rename routing intent astray.</summary>
    public string[] ForbidToolCalls { get; init; } = [];

    /// <summary>Whether the turn must be intercepted by the human-in-the-loop gate.</summary>
    public bool ExpectApproval { get; init; }

    public string[] ReplyMustContain { get; init; } = [];

    public string[] ReplyMustNotContain { get; init; } = [];

    /// <summary>Matters created before the turn, so a case can assume its fixtures exist.</summary>
    public string[] SeedMatters { get; init; } = [];

    [JsonExtensionData]
    public Dictionary<string, object>? Unknown { get; init; }
}
