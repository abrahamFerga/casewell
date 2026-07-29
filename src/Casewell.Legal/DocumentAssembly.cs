using System.Text;

namespace Cortex.Modules.Legal;

/// <summary>
/// Assembles rendered clauses into one document draft: heading, numbered sections, and the
/// not-legal-advice footer. Pure — the tool resolves the template and clauses, this composes text.
/// </summary>
public static class DocumentAssembly
{
    public const string Footer =
        "---\nThis document was assembled from the firm's clause library as a starting template. " +
        "It is not legal advice; have a licensed attorney review before use.";

    public static string Compose(string title, IReadOnlyList<RenderedClause> clauses)
    {
        var sb = new StringBuilder();
        sb.AppendLine(title.Trim());
        sb.AppendLine(new string('=', Math.Min(title.Trim().Length, 80)));
        sb.AppendLine();

        for (var i = 0; i < clauses.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {clauses[i].Title}");
            sb.AppendLine();
            sb.AppendLine(clauses[i].Body.Trim());
            sb.AppendLine();
        }

        sb.Append(Footer);
        return sb.ToString();
    }
}
