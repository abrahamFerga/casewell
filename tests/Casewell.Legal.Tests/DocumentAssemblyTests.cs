using Cortex.Modules.Legal;

namespace Cortex.Modules.Legal.Tests;

public sealed class DocumentAssemblyTests
{
    [Fact]
    public void Compose_NumbersSectionsInOrder_AndKeepsTheHeading()
    {
        var text = DocumentAssembly.Compose(
            "Mutual NDA between Acme Corp and Beta LLC",
            [
                new RenderedClause("Confidentiality", "Protection", "Keep it secret."),
                new RenderedClause("Termination", "Lifecycle", "Thirty days notice."),
                new RenderedClause("Governing Law", "General", "Agreed jurisdiction."),
            ]);

        Assert.StartsWith("Mutual NDA between Acme Corp and Beta LLC", text, StringComparison.Ordinal);
        var confidentiality = text.IndexOf("1. Confidentiality", StringComparison.Ordinal);
        var termination = text.IndexOf("2. Termination", StringComparison.Ordinal);
        var governing = text.IndexOf("3. Governing Law", StringComparison.Ordinal);
        Assert.True(confidentiality > 0 && termination > confidentiality && governing > termination);
        Assert.Contains("Keep it secret.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_AlwaysAppendsTheNotLegalAdviceFooter()
    {
        var text = DocumentAssembly.Compose("Anything", []);

        Assert.EndsWith(DocumentAssembly.Footer, text, StringComparison.Ordinal);
        Assert.Contains("not legal advice", text, StringComparison.Ordinal);
    }
}
