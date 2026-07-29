using Cortex.Modules.Legal;

namespace Cortex.Modules.Legal.Tests;

/// <summary>
/// Pins the alpha.14 hand-edit surface: the tab editors, the weekly-hours chart, and the
/// first-run setup wizard. Like the tool list, the tab order grows feature by feature — a
/// change here is a product decision, not an accident.
/// </summary>
public sealed class ManualSurfaceTests
{
    private static Sdk.ModuleManifest Manifest => new LegalModule().Manifest;

    [Fact]
    public void Tabs_ArePinned_InOrder()
    {
        Assert.Equal(
            ["chat", "matters", "clients", "calendar", "tasks", "time", "hours", "clauses", "playbook"],
            Manifest.Tabs.OrderBy(t => t.Order).Select(t => t.Id));
    }

    [Fact]
    public void HandEditableTabs_DeclareEditors_BehindLegalManage()
    {
        var manifest = Manifest;

        // The practice's working records are hand-editable behind legal.manage — a person on a
        // form acts directly, so RBAC is the whole gate (no AI approval step).
        foreach (var id in new[] { "matters", "clients", "calendar", "tasks", "time" })
        {
            var editor = manifest.Tabs.First(t => t.Id == id).Editor;
            Assert.NotNull(editor);
            Assert.Equal(LegalModule.Manage, editor.Permission);
            Assert.NotNull(editor.DeleteEndpoint);
            Assert.NotNull(editor.KeyField);
        }

        // Library curation keeps its own, pre-existing permission.
        Assert.Equal(LegalModule.ManageLibrary, manifest.Tabs.First(t => t.Id == "clauses").Editor!.Permission);
        Assert.Equal(LegalModule.ManageLibrary, manifest.Tabs.First(t => t.Id == "playbook").Editor!.Permission);

        // Chat and the chart never grow forms.
        Assert.Null(manifest.Tabs.First(t => t.Id == "chat").Editor);
        Assert.Null(manifest.Tabs.First(t => t.Id == "hours").Editor);
    }

    [Fact]
    public void HoursTab_ChartsBillableHoursPerWeek()
    {
        var hours = Manifest.Tabs.First(t => t.Id == "hours");

        Assert.Equal("/api/legal/time/weekly", hours.DataEndpoint);
        Assert.NotNull(hours.Chart);
        Assert.Equal("weekOf", hours.Chart.XField);
        Assert.Equal("hours", hours.Chart.YField);
    }

    [Fact]
    public void Onboarding_ProbesMatters_BehindLegalManage_WithPinnedSteps()
    {
        var onboarding = Manifest.Onboarding;

        Assert.NotNull(onboarding);
        Assert.Equal("/api/legal/matters", onboarding.ProbeEndpoint);
        Assert.Equal(LegalModule.Manage, onboarding.Permission);
        Assert.Equal(
            ["welcome", "firm-basics", "first-client", "first-matter", "key-deadlines", "done"],
            onboarding.Steps.Select(s => s.Id));

        // Every form step posts to a manual-CRUD endpoint some tab editor also uses: the wizard
        // is a guided front on the same write path, never a second one.
        var editorEndpoints = Manifest.Tabs
            .Where(t => t.Editor is not null)
            .Select(t => t.Editor!.UpsertEndpoint)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(
            onboarding.Steps.Where(s => s.Kind == "form"),
            s =>
            {
                Assert.NotNull(s.Endpoint);
                Assert.Contains(s.Endpoint, editorEndpoints);
            });

        // The deadlines step docket entries as deadlines without asking.
        var deadlines = onboarding.Steps.First(s => s.Id == "key-deadlines");
        Assert.Equal("deadline", deadlines.Preset["type"]);
    }

    [Fact]
    public void WeekOf_BucketsAnyDay_ToItsMonday()
    {
        var monday = new DateOnly(2026, 7, 6);

        Assert.Equal(monday, TimeCapture.WeekOf(monday));
        Assert.Equal(monday, TimeCapture.WeekOf(new DateOnly(2026, 7, 8)));   // Wednesday
        Assert.Equal(monday, TimeCapture.WeekOf(new DateOnly(2026, 7, 12)));  // Sunday
        Assert.Equal(monday.AddDays(7), TimeCapture.WeekOf(new DateOnly(2026, 7, 13)));
    }
}
