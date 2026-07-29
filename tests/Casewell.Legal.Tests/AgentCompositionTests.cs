using Cortex.Modules.Legal;

namespace Cortex.Modules.Legal.Tests;

/// <summary>
/// The module's chat personas stay coherent with its own manifest: every tool a persona selects
/// exists (a renamed tool would otherwise silently vanish from the persona), platform tools are
/// the known set, and the shipped workflow chains declared agents only.
/// </summary>
public sealed class AgentCompositionTests
{
    // Platform-level tools a persona may reference beyond the module's own manifest.
    private static readonly string[] PlatformTools = ["generate_pdf", "read_document", "list_documents", "ocr_document", "search_knowledge"];

    [Fact]
    public void EveryPersonaToolExists()
    {
        var manifest = new LegalModule().Manifest;
        var known = manifest.Tools.Select(t => t.Name).Concat(PlatformTools).ToHashSet(StringComparer.Ordinal);

        foreach (var agent in manifest.Agents)
        {
            var unknown = (agent.ToolNames ?? []).Where(t => !known.Contains(t)).ToArray();
            Assert.True(unknown.Length == 0,
                $"Agent '{agent.Name}' selects unknown tool(s): {string.Join(", ", unknown)}");
        }
    }

    [Fact]
    public void PersonasAreDisjointWhereItMatters()
    {
        var manifest = new LegalModule().Manifest;
        var drafter = manifest.Agents.Single(a => a.Name == "drafter");
        var docketing = manifest.Agents.Single(a => a.Name == "docketing");

        // The docketing clerk must not draft; the drafter must not docket.
        Assert.DoesNotContain(docketing.ToolNames!, t => t.Contains("clause", StringComparison.Ordinal));
        Assert.DoesNotContain(docketing.ToolNames!, t => t.Contains("template", StringComparison.Ordinal));
        Assert.DoesNotContain(drafter.ToolNames!, t => t.Contains("event", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkflowStepsReferenceDeclaredAgents()
    {
        var manifest = new LegalModule().Manifest;
        var agents = manifest.Agents.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var workflow in manifest.Workflows)
        {
            Assert.NotEmpty(workflow.AgentNames);
            foreach (var step in workflow.AgentNames)
            {
                Assert.Contains(step, agents);
            }
        }
    }
}
