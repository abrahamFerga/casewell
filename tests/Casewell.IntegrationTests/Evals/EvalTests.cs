using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Casewell.IntegrationTests.Evals;

/// <summary>
/// Rung 4 — golden conversation evals. Each case in <c>cases/*.json</c> drives one real AG-UI turn
/// and asserts routing, the approval gate, and the protocol. The Mock provider selects tools by
/// name-token match, so these prove the <em>platform contract</em>, not model reasoning quality.
/// </summary>
[Collection("api")]
public sealed class EvalTests(IntegrationFixture fixture)
{
    private static readonly JsonSerializerOptions CaseJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static TheoryData<string> CaseFiles()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(CaseDirectory(), "*.json").OrderBy(f => f))
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CaseFiles))]
    public async Task Case_holds(string fileName)
    {
        var json = await File.ReadAllTextAsync(Path.Combine(CaseDirectory(), fileName));
        var @case = JsonSerializer.Deserialize<EvalCase>(json, CaseJson)
                    ?? throw new InvalidOperationException($"{fileName} did not deserialize.");

        // A typo'd field would otherwise assert nothing and pass forever.
        var unknownFields = @case.Unknown is null ? [] : @case.Unknown.Keys.ToArray();
        Assert.True(
            unknownFields.Length == 0,
            $"{fileName} has unknown field(s): {string.Join(", ", unknownFields)}");

        using var client = fixture.AdminClient(roles: @case.Role, subject: $"eval-{@case.Name}");

        foreach (var matter in @case.SeedMatters)
        {
            var seeded = await client.PostAsJsonAsync("/api/legal/matters", new
            {
                name = matter,
                clientName = matter,
                practiceArea = "Corporate",
            });
            seeded.EnsureSuccessStatusCode();
        }

        var response = await client.PostAsJsonAsync($"/api/agui/{@case.Module}", new
        {
            messages = new[] { new { id = "m1", role = "user", content = @case.Message } },
        });
        response.EnsureSuccessStatusCode();

        var events = (await response.Content.ReadAsStringAsync())
            .Split('\n')
            .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
            .Select(l => JsonSerializer.Deserialize<JsonElement>(l["data:".Length..].Trim()))
            .ToList();

        var types = events.Select(e => e.GetProperty("type").GetString()!).ToList();
        var toolCalls = events
            .Where(e => e.GetProperty("type").GetString() == "TOOL_CALL_START")
            .Select(e => e.GetProperty("toolCallName").GetString()!)
            .ToList();
        var reply = string.Concat(events
            .Where(e => e.GetProperty("type").GetString() == "TEXT_MESSAGE_CONTENT")
            .Select(e => e.GetProperty("delta").GetString()));

        // Implicit in every case: the protocol completed and nothing errored.
        Assert.Contains("RUN_STARTED", types);
        Assert.Contains("RUN_FINISHED", types);
        Assert.DoesNotContain("RUN_ERROR", types);

        foreach (var expected in @case.ExpectToolCalls)
        {
            Assert.Contains(expected, toolCalls);
        }

        foreach (var forbidden in @case.ForbidToolCalls)
        {
            Assert.DoesNotContain(forbidden, toolCalls);
        }

        var gated = events.Any(e =>
            e.GetProperty("type").GetString() == "CUSTOM" &&
            e.GetProperty("name").GetString() == "approval_required");
        Assert.Equal(@case.ExpectApproval, gated);

        foreach (var fragment in @case.ReplyMustContain)
        {
            Assert.Contains(fragment, reply, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var fragment in @case.ReplyMustNotContain)
        {
            Assert.DoesNotContain(fragment, reply, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string CaseDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "Evals", "cases");
}
