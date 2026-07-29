using System.Text.Json;

namespace Cortex.Modules.Legal;

/// <summary>
/// Pure helpers for curating the firm's library from chat: slug derivation (same convention the
/// seed and templates use) and the delete guard that keeps a clause from being removed while a
/// document template still assembles from it.
/// </summary>
public static class LibraryCuration
{
    /// <summary>"Data Protection" → "data-protection" — the stable per-tenant clause identity.</summary>
    public static string Slugify(string clauseType) =>
        string.Join('-', clauseType.Trim().ToLowerInvariant()
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Names of the templates whose clause list references <paramref name="slug"/>.</summary>
    public static IReadOnlyList<string> TemplatesReferencing(
        string slug, IEnumerable<(string Name, string ClauseSlugsJson)> templates)
    {
        var hits = new List<string>();
        foreach (var (name, json) in templates)
        {
            string[] slugs;
            try
            {
                slugs = JsonSerializer.Deserialize<string[]>(json) ?? [];
            }
            catch (JsonException)
            {
                continue; // a corrupt template list never blocks curation of unrelated clauses
            }

            if (slugs.Contains(slug, StringComparer.OrdinalIgnoreCase))
            {
                hits.Add(name);
            }
        }

        return hits;
    }
}
