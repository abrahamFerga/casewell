using Cortex.Modules.Legal;

namespace Cortex.Modules.Legal.Tests;

/// <summary>
/// The curation helpers behind save_clause / remove_clause: slugs follow the seed convention, and
/// the delete guard names every document template that still assembles from a clause — removing a
/// referenced clause would silently break those drafts.
/// </summary>
public sealed class LibraryCurationTests
{
    [Theory]
    [InlineData("Data Protection", "data-protection")]
    [InlineData("  confidentiality  ", "confidentiality")]
    [InlineData("NON   Compete", "non-compete")]
    public void Slugify_MatchesTheSeedConvention(string input, string expected)
    {
        Assert.Equal(expected, LibraryCuration.Slugify(input));
    }

    [Fact]
    public void TemplatesReferencing_NamesEveryReferencingTemplate_CaseInsensitively()
    {
        (string, string)[] templates =
        [
            ("mutual-nda", """["confidentiality","termination"]"""),
            ("consulting-agreement", """["CONFIDENTIALITY","payment-terms"]"""),
            ("engagement-letter", """["engagement"]"""),
        ];

        var hits = LibraryCuration.TemplatesReferencing("confidentiality", templates);

        Assert.Equal(["mutual-nda", "consulting-agreement"], hits);
        Assert.Empty(LibraryCuration.TemplatesReferencing("non-compete", templates));
    }

    [Fact]
    public void CorruptTemplateJson_NeverBlocksCurationOfOtherClauses()
    {
        (string, string)[] templates = [("broken", "not json"), ("ok", """["governing-law"]""")];

        Assert.Empty(LibraryCuration.TemplatesReferencing("confidentiality", templates));
        Assert.Equal(["ok"], LibraryCuration.TemplatesReferencing("governing-law", templates));
    }
}
