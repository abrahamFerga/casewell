using System.Globalization;
using Cortex.Application.Authorization;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Cortex.Modules.Legal.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Modules.Legal;

/// <summary>
/// The hand-edit surface behind the tab editors and the setup wizard: AI-first, not chat-only.
/// A person acting directly on a form is not an agent proposing an action, so there is no
/// approval gate here — RBAC is the whole gate (<see cref="LegalModule.Manage"/>), and every
/// handler still enforces the domain's invariants: tenant-scoped writes, ethical walls, the
/// docket-number scheme, and the matter close-out check (which a form can never force past —
/// forcing stays a chat-only act behind an explicit user confirmation).
/// </summary>
internal static class ManualCrudEndpoints
{
    public static void MapManualCrudEndpoints(this RouteGroupBuilder group)
    {
        var view = PermissionRequirement.PolicyName(LegalModule.ViewMatters);
        var manage = PermissionRequirement.PolicyName(LegalModule.Manage);

        // ── Clients: the firm's contact book ────────────────────────────────────────────────

        group.MapGet("/clients", async (LegalDbContext db, CancellationToken cancellationToken) =>
            {
                var matterCounts = await db.Matters
                    .Where(m => m.ClientName != null)
                    .GroupBy(m => m.ClientName!)
                    .Select(g => new { Name = g.Key, Count = g.Count() })
                    .ToDictionaryAsync(g => g.Name, g => g.Count, StringComparer.OrdinalIgnoreCase, cancellationToken);
                var clients = (await db.Clients.OrderBy(c => c.Name).Take(500).ToListAsync(cancellationToken))
                    .Select(c => new ClientDto(
                        c.Id, c.Name, c.Email, c.Phone, c.Organization,
                        matterCounts.GetValueOrDefault(c.Name), c.Notes));
                return Results.Ok(clients);
            })
            .RequireAuthorization(view)
            .WithName("Legal_GetClients");

        group.MapPost("/clients", async (
                ClientUpsert body, LegalDbContext db, ITenantContext tenant, CancellationToken cancellationToken) =>
            {
                var name = body.Name?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    return Results.BadRequest(new { error = "A client needs a name." });
                }

                var existing = await db.Clients.FirstOrDefaultAsync(
                    c => EF.Functions.ILike(c.Name, name), cancellationToken);
                if (existing is null)
                {
                    existing = new Client { TenantId = tenant.RequireTenantId(), Name = name };
                    db.Clients.Add(existing);
                }

                // Blank optional fields never arrive (the shell omits them), so null means
                // "leave as is" — an edit updates only what the user typed.
                existing.Email = body.Email?.Trim() ?? existing.Email;
                existing.Phone = body.Phone?.Trim() ?? existing.Phone;
                existing.Organization = body.Organization?.Trim() ?? existing.Organization;
                existing.Notes = body.Notes?.Trim() ?? existing.Notes;
                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(new { existing.Id, existing.Name });
            })
            .RequireAuthorization(manage)
            .WithName("Legal_UpsertClient");

        group.MapDelete("/clients/{id:guid}", async (Guid id, LegalDbContext db, CancellationToken cancellationToken) =>
            {
                var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
                if (client is null)
                {
                    return Results.NotFound();
                }

                // Deliberately unconditional: the contact book is not the engagement record, and
                // matters reference clients by display name — history stays intact.
                db.Clients.Remove(client);
                await db.SaveChangesAsync(cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization(manage)
            .WithName("Legal_DeleteClient");

        // ── Matters: open and maintain engagements by hand ──────────────────────────────────

        group.MapPost("/matters", async (
                MatterUpsert body, LegalDbContext db, ITenantContext tenant, ICurrentUser current,
                CancellationToken cancellationToken) =>
            {
                var name = body.Name?.Trim();
                if (string.IsNullOrEmpty(name))
                {
                    return Results.BadRequest(new { error = "A matter needs a name." });
                }

                string? area = null;
                if (!string.IsNullOrWhiteSpace(body.PracticeArea))
                {
                    area = PracticeAreas.Normalize(body.PracticeArea);
                    if (area is null)
                    {
                        return Results.BadRequest(new { error = $"'{body.PracticeArea}' is not a recognized practice area. Use one of: {PracticeAreas.Listed}." });
                    }
                }

                MatterStatus? status = null;
                if (!string.IsNullOrWhiteSpace(body.Status))
                {
                    status = ParseStatus(body.Status);
                    if (status is null)
                    {
                        return Results.BadRequest(new { error = $"'{body.Status}' is not a matter status. Use 'open', 'on-hold', or 'closed'." });
                    }
                }

                var matter = await FindAccessibleMatterAsync(db, current, name, cancellationToken);
                if (matter is null)
                {
                    matter = new Matter
                    {
                        TenantId = tenant.RequireTenantId(),
                        Name = name,
                        ClientName = body.ClientName?.Trim(),
                        ClientEmail = body.ClientEmail?.Trim(),
                        PracticeArea = area,
                    };
                    // A brand-new matter has no obligations, so any requested status is safe.
                    if (status is not null)
                    {
                        matter.ApplyStatus(status.Value, DateTimeOffset.UtcNow);
                    }

                    db.Matters.Add(matter);
                    try
                    {
                        await MatterNumbering.NumberAndSaveAsync(db, matter, cancellationToken);
                    }
                    catch (DbUpdateException)
                    {
                        // The (TenantId, Name) unique index: the name is taken — including by a
                        // walled matter, which must stay indistinguishable from a missing one.
                        return Results.BadRequest(new { error = $"A matter named '{name}' already exists." });
                    }

                    return Results.Ok(new { matter.Id, matter.Name, matter.MatterNumber, created = true });
                }

                matter.ClientName = body.ClientName?.Trim() ?? matter.ClientName;
                matter.ClientEmail = body.ClientEmail?.Trim() ?? matter.ClientEmail;
                matter.PracticeArea = area ?? matter.PracticeArea;
                if (status is not null)
                {
                    if (status == MatterStatus.Closed && matter.Status != MatterStatus.Closed)
                    {
                        // The close-out gate, without the tool's force escape hatch: a form click
                        // is not the explicit confirmation forcing requires.
                        var openEvents = await db.MatterEvents
                            .CountAsync(e => e.MatterId == matter.Id && e.CompletedAt == null, cancellationToken);
                        var openTasks = await db.MatterTasks
                            .CountAsync(t => t.MatterId == matter.Id && t.CompletedAt == null, cancellationToken);
                        if (openEvents > 0 || openTasks > 0)
                        {
                            return Results.BadRequest(new
                            {
                                error = $"Matter '{matter.Name}' still has {openEvents} open event(s) and {openTasks} open task(s). " +
                                        "Complete them first — close-out cannot be forced from here.",
                            });
                        }
                    }

                    matter.ApplyStatus(status.Value, DateTimeOffset.UtcNow);
                }

                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(new { matter.Id, matter.Name, matter.MatterNumber, created = false });
            })
            .RequireAuthorization(manage)
            .WithName("Legal_UpsertMatter");

        group.MapDelete("/matters/{id:guid}", async (
                Guid id, LegalDbContext db, ICurrentUser current, CancellationToken cancellationToken) =>
            {
                var matter = await db.Matters.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
                if (matter is null || !matter.IsAccessibleTo(current.UserId))
                {
                    return Results.NotFound();
                }

                // Only a mistake deletes: a matter that accumulated any record is the firm's
                // history and closes instead (which runs the close-out check) — deletion is not
                // a back door around it.
                var hasHistory =
                    await db.MatterDocuments.AnyAsync(d => d.MatterId == id, cancellationToken) ||
                    await db.MatterEvents.AnyAsync(e => e.MatterId == id, cancellationToken) ||
                    await db.MatterTasks.AnyAsync(t => t.MatterId == id, cancellationToken) ||
                    await db.TimeEntries.AnyAsync(t => t.MatterId == id, cancellationToken) ||
                    await db.MatterParties.AnyAsync(p => p.MatterId == id, cancellationToken) ||
                    await db.ConflictAttestations.AnyAsync(a => a.MatterId == id, cancellationToken);
                if (hasHistory)
                {
                    return Results.Conflict(new { error = $"Matter '{matter.Name}' has documents, dates, tasks, time, or attestations on record — close it instead of deleting." });
                }

                db.Matters.Remove(matter);
                await db.SaveChangesAsync(cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization(manage)
            .WithName("Legal_DeleteMatter");

        // ── Deadlines: docket and correct calendar events by hand ───────────────────────────

        group.MapPost("/events", async (
                EventUpsert body, LegalDbContext db, ITenantContext tenant, ICurrentUser current,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(body.Title))
                {
                    return Results.BadRequest(new { error = "An event needs a title." });
                }

                DateTimeOffset? startsAt = null;
                if (!string.IsNullOrWhiteSpace(body.StartsAt))
                {
                    if (!DateTimeOffset.TryParse(body.StartsAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    {
                        return Results.BadRequest(new { error = $"'{body.StartsAt}' is not a date I can parse — use an ISO date like 2026-08-14 or 2026-08-14 09:00." });
                    }

                    startsAt = parsed;
                }

                if (body.Id is { } eventId)
                {
                    var existing = await db.MatterEvents.FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);
                    if (existing is null || !await MatterAllowsAsync(db, current, existing.MatterId, cancellationToken))
                    {
                        return Results.NotFound();
                    }

                    existing.Title = body.Title.Trim();
                    existing.Type = body.Type is null ? existing.Type : MatterEvent.NormalizeType(body.Type);
                    existing.StartsAt = startsAt ?? existing.StartsAt;
                    existing.Notes = body.Notes?.Trim() ?? existing.Notes;
                    await db.SaveChangesAsync(cancellationToken);
                    return Results.Ok(new { existing.Id });
                }

                if (string.IsNullOrWhiteSpace(body.MatterName))
                {
                    return Results.BadRequest(new { error = "Name the matter the event belongs to." });
                }

                if (startsAt is null)
                {
                    return Results.BadRequest(new { error = "An event needs a date — use an ISO date like 2026-08-14." });
                }

                var matter = await FindAccessibleMatterAsync(db, current, body.MatterName, cancellationToken);
                if (matter is null)
                {
                    return Results.BadRequest(new { error = $"No matter named '{body.MatterName.Trim()}' exists." });
                }

                var evt = new MatterEvent
                {
                    TenantId = tenant.RequireTenantId(),
                    MatterId = matter.Id,
                    Title = body.Title.Trim(),
                    Type = MatterEvent.NormalizeType(body.Type),
                    StartsAt = startsAt.Value,
                    Notes = body.Notes?.Trim(),
                    CreatedByUserId = current.UserId,
                };
                db.MatterEvents.Add(evt);
                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(new { evt.Id });
            })
            .RequireAuthorization(manage)
            .WithName("Legal_UpsertEvent");

        group.MapDelete("/events/{id:guid}", async (
                Guid id, LegalDbContext db, ICurrentUser current, CancellationToken cancellationToken) =>
            {
                var evt = await db.MatterEvents.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
                if (evt is null || !await MatterAllowsAsync(db, current, evt.MatterId, cancellationToken))
                {
                    return Results.NotFound();
                }

                db.MatterEvents.Remove(evt);
                await db.SaveChangesAsync(cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization(manage)
            .WithName("Legal_DeleteEvent");

        // ── Tasks: the working list, maintainable by hand ───────────────────────────────────

        group.MapGet("/tasks", async (
                LegalDbContext db, ICurrentUser current, CancellationToken cancellationToken) =>
            {
                var matters = await AccessibleMattersAsync(db, current, cancellationToken);
                var recently = DateTimeOffset.UtcNow.AddDays(-14);
                var tasks = (await db.MatterTasks
                        .Where(t => t.CompletedAt == null || t.CompletedAt >= recently)
                        .Take(500)
                        .ToListAsync(cancellationToken))
                    .Where(t => matters.ContainsKey(t.MatterId))
                    .OrderBy(t => t.CompletedAt != null)
                    .ThenBy(t => t.DueOn ?? DateOnly.MaxValue)
                    .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                    .Select(t => new TaskDto(
                        t.Id, matters[t.MatterId], t.Title, t.AssignedTo,
                        t.DueOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        t.CompletedAt is null ? "open" : "done", t.Notes));
                return Results.Ok(tasks);
            })
            .RequireAuthorization(view)
            .WithName("Legal_GetTasks");

        group.MapPost("/tasks", async (
                TaskUpsert body, LegalDbContext db, ITenantContext tenant, ICurrentUser current,
                CancellationToken cancellationToken) =>
            {
                if (string.IsNullOrWhiteSpace(body.Title))
                {
                    return Results.BadRequest(new { error = "A task needs a title — what needs doing?" });
                }

                DateOnly? dueOn = null;
                if (!string.IsNullOrWhiteSpace(body.DueOn))
                {
                    if (!DateOnly.TryParse(body.DueOn, CultureInfo.InvariantCulture, out var parsed))
                    {
                        return Results.BadRequest(new { error = $"'{body.DueOn}' is not a date I can parse — use an ISO date like 2026-08-01." });
                    }

                    dueOn = parsed;
                }

                bool? done = body.Status?.Trim().ToLowerInvariant() switch
                {
                    null or "" => null,
                    "done" or "completed" or "closed" => true,
                    "open" or "pending" => false,
                    _ => (bool?)null,
                };
                if (done is null && !string.IsNullOrWhiteSpace(body.Status))
                {
                    return Results.BadRequest(new { error = $"'{body.Status}' is not a task status. Use 'open' or 'done'." });
                }

                if (body.Id is { } taskId)
                {
                    var existing = await db.MatterTasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);
                    if (existing is null || !await MatterAllowsAsync(db, current, existing.MatterId, cancellationToken))
                    {
                        return Results.NotFound();
                    }

                    existing.Title = body.Title.Trim();
                    existing.AssignedTo = body.AssignedTo?.Trim() ?? existing.AssignedTo;
                    existing.DueOn = dueOn ?? existing.DueOn;
                    existing.Notes = body.Notes?.Trim() ?? existing.Notes;
                    if (done is not null)
                    {
                        existing.CompletedAt = done.Value ? existing.CompletedAt ?? DateTimeOffset.UtcNow : null;
                    }

                    await db.SaveChangesAsync(cancellationToken);
                    return Results.Ok(new { existing.Id });
                }

                if (string.IsNullOrWhiteSpace(body.MatterName))
                {
                    return Results.BadRequest(new { error = "Name the matter the task belongs to." });
                }

                var matter = await FindAccessibleMatterAsync(db, current, body.MatterName, cancellationToken);
                if (matter is null)
                {
                    return Results.BadRequest(new { error = $"No matter named '{body.MatterName.Trim()}' exists." });
                }

                var task = new MatterTask
                {
                    TenantId = tenant.RequireTenantId(),
                    MatterId = matter.Id,
                    Title = body.Title.Trim(),
                    AssignedTo = body.AssignedTo?.Trim(),
                    DueOn = dueOn,
                    Notes = body.Notes?.Trim(),
                    CompletedAt = done == true ? DateTimeOffset.UtcNow : null,
                    CreatedByUserId = current.UserId,
                };
                db.MatterTasks.Add(task);
                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(new { task.Id });
            })
            .RequireAuthorization(manage)
            .WithName("Legal_UpsertTask");

        group.MapDelete("/tasks/{id:guid}", async (
                Guid id, LegalDbContext db, ICurrentUser current, CancellationToken cancellationToken) =>
            {
                var task = await db.MatterTasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
                if (task is null || !await MatterAllowsAsync(db, current, task.MatterId, cancellationToken))
                {
                    return Results.NotFound();
                }

                db.MatterTasks.Remove(task);
                await db.SaveChangesAsync(cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization(manage)
            .WithName("Legal_DeleteTask");

        // ── Time entries: correctable capture ────────────────────────────────────────────────

        group.MapGet("/time-entries", async (
                LegalDbContext db, ICurrentUser current, CancellationToken cancellationToken) =>
            {
                var matters = await AccessibleMattersAsync(db, current, cancellationToken);
                var entries = (await db.TimeEntries
                        .OrderByDescending(t => t.WorkedOn)
                        .Take(500)
                        .ToListAsync(cancellationToken))
                    .Where(t => matters.ContainsKey(t.MatterId))
                    .Select(t => new TimeEntryDto(
                        t.Id, t.WorkedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        matters[t.MatterId], t.UserDisplay, t.Hours, t.Billable ? "yes" : "no", t.Description));
                return Results.Ok(entries);
            })
            .RequireAuthorization(view)
            .WithName("Legal_GetTimeEntries");

        group.MapPost("/time-entries", async (
                TimeEntryUpsert body, LegalDbContext db, ITenantContext tenant, ICurrentUser current,
                CancellationToken cancellationToken) =>
            {
                if (!TimeCapture.HoursAreValid((double)body.Hours))
                {
                    return Results.BadRequest(new { error = "Hours must be greater than 0 and at most 24 per entry." });
                }

                if (string.IsNullOrWhiteSpace(body.Description))
                {
                    return Results.BadRequest(new { error = "A time entry needs a description — it becomes the narrative line on the bill." });
                }

                DateOnly? workedOn = null;
                if (!string.IsNullOrWhiteSpace(body.WorkedOn))
                {
                    if (!DateOnly.TryParse(body.WorkedOn, CultureInfo.InvariantCulture, out var parsed))
                    {
                        return Results.BadRequest(new { error = $"'{body.WorkedOn}' is not a date I can parse — use an ISO date like 2026-07-10." });
                    }

                    workedOn = parsed;
                }

                bool? billable = body.Billable?.Trim().ToLowerInvariant() switch
                {
                    null or "" => null,
                    "yes" or "y" or "true" => true,
                    "no" or "n" or "false" => false,
                    _ => (bool?)null,
                };
                if (billable is null && !string.IsNullOrWhiteSpace(body.Billable))
                {
                    return Results.BadRequest(new { error = $"'{body.Billable}' is not a billable flag. Use 'yes' or 'no'." });
                }

                if (body.Id is { } entryId)
                {
                    var existing = await db.TimeEntries.FirstOrDefaultAsync(t => t.Id == entryId, cancellationToken);
                    if (existing is null || !await MatterAllowsAsync(db, current, existing.MatterId, cancellationToken))
                    {
                        return Results.NotFound();
                    }

                    existing.Hours = body.Hours;
                    existing.Description = body.Description.Trim();
                    existing.WorkedOn = workedOn ?? existing.WorkedOn;
                    existing.Billable = billable ?? existing.Billable;
                    await db.SaveChangesAsync(cancellationToken);
                    return Results.Ok(new { existing.Id });
                }

                if (string.IsNullOrWhiteSpace(body.MatterName))
                {
                    return Results.BadRequest(new { error = "Name the matter the time was spent on." });
                }

                var matter = await FindAccessibleMatterAsync(db, current, body.MatterName, cancellationToken);
                if (matter is null)
                {
                    return Results.BadRequest(new { error = $"No matter named '{body.MatterName.Trim()}' exists." });
                }

                var entry = new TimeEntry
                {
                    TenantId = tenant.RequireTenantId(),
                    MatterId = matter.Id,
                    UserId = current.UserId,
                    UserDisplay = current.DisplayName,
                    Hours = body.Hours,
                    Description = body.Description.Trim(),
                    WorkedOn = workedOn ?? DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime),
                    Billable = billable ?? true,
                };
                db.TimeEntries.Add(entry);
                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(new { entry.Id });
            })
            .RequireAuthorization(manage)
            .WithName("Legal_UpsertTimeEntry");

        group.MapDelete("/time-entries/{id:guid}", async (
                Guid id, LegalDbContext db, ICurrentUser current, CancellationToken cancellationToken) =>
            {
                var entry = await db.TimeEntries.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
                if (entry is null || !await MatterAllowsAsync(db, current, entry.MatterId, cancellationToken))
                {
                    return Results.NotFound();
                }

                db.TimeEntries.Remove(entry);
                await db.SaveChangesAsync(cancellationToken);
                return Results.NoContent();
            })
            .RequireAuthorization(manage)
            .WithName("Legal_DeleteTimeEntry");

        // ── Billable hours per week: the Hours chart's rows ─────────────────────────────────

        group.MapGet("/time/weekly", async (
                LegalDbContext db, ICurrentUser current, CancellationToken cancellationToken) =>
            {
                var matters = await AccessibleMattersAsync(db, current, cancellationToken);
                var since = TimeCapture.WeekOf(DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime)).AddDays(-26 * 7);
                var weekly = (await db.TimeEntries
                        .Where(t => t.Billable && t.WorkedOn >= since)
                        .ToListAsync(cancellationToken))
                    .Where(t => matters.ContainsKey(t.MatterId))
                    .GroupBy(t => TimeCapture.WeekOf(t.WorkedOn))
                    .OrderBy(g => g.Key)
                    .Select(g => new
                    {
                        weekOf = g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        hours = g.Sum(t => t.Hours),
                    });
                return Results.Ok(weekly);
            })
            .RequireAuthorization(view)
            .WithName("Legal_GetWeeklyHours");
    }

    /// <summary>Tenant-scoped, wall-filtered id→name map — the same stance the events endpoint takes.</summary>
    private static async Task<Dictionary<Guid, string>> AccessibleMattersAsync(
        LegalDbContext db, ICurrentUser current, CancellationToken cancellationToken)
    {
        return (await db.Matters
                .Select(m => new { m.Id, m.Name, m.RestrictedUserIdsJson })
                .ToListAsync(cancellationToken))
            .Where(m => Matter.WallAllows(m.RestrictedUserIdsJson, current.UserId))
            .ToDictionary(m => m.Id, m => m.Name);
    }

    private static async Task<bool> MatterAllowsAsync(
        LegalDbContext db, ICurrentUser current, Guid matterId, CancellationToken cancellationToken)
    {
        var matter = await db.Matters.FirstOrDefaultAsync(m => m.Id == matterId, cancellationToken);
        return matter is not null && matter.IsAccessibleTo(current.UserId);
    }

    private static async Task<Matter?> FindAccessibleMatterAsync(
        LegalDbContext db, ICurrentUser current, string name, CancellationToken cancellationToken)
    {
        var normalized = name.Trim();
        var matter = await db.Matters.FirstOrDefaultAsync(
            m => EF.Functions.ILike(m.Name, normalized), cancellationToken);
        return matter is not null && matter.IsAccessibleTo(current.UserId) ? matter : null;
    }

    private static MatterStatus? ParseStatus(string status) =>
        status.Trim().Replace("-", "", StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "open" or "reopen" or "reopened" => MatterStatus.Open,
            "onhold" or "hold" or "paused" => MatterStatus.OnHold,
            "closed" or "close" => MatterStatus.Closed,
            _ => null,
        };

    private sealed record ClientDto(
        Guid Id, string Name, string? Email, string? Phone, string? Organization, int Matters, string? Notes);

    private sealed record TaskDto(
        Guid Id, string MatterName, string Title, string? AssignedTo, string? DueOn, string Status, string? Notes);

    private sealed record TimeEntryDto(
        Guid Id, string WorkedOn, string MatterName, string? Who, decimal Hours, string Billable, string Description);

    /// <summary>Create or update a client (matched case-insensitively by name).</summary>
    internal sealed record ClientUpsert(string? Name, string? Email, string? Phone, string? Organization, string? Notes);

    /// <summary>Create or update a matter (matched by name; creation assigns the docket number).</summary>
    internal sealed record MatterUpsert(string? Name, string? ClientName, string? ClientEmail, string? PracticeArea, string? Status);

    /// <summary>Add (no id) or edit (id from the row) a calendar event. Property names bind the
    /// camelCase field names the tab editor and wizard post — <c>startsAt</c> matches the row.</summary>
    internal sealed record EventUpsert(Guid? Id, string? MatterName, string? Title, string? Type, string? StartsAt, string? Notes);

    /// <summary>Add (no id) or edit (id from the row) a matter task.</summary>
    internal sealed record TaskUpsert(Guid? Id, string? MatterName, string? Title, string? AssignedTo, string? DueOn, string? Status, string? Notes);

    /// <summary>Add (no id) or edit (id from the row) a time entry.</summary>
    internal sealed record TimeEntryUpsert(Guid? Id, string? MatterName, decimal Hours, string? Description, string? WorkedOn, string? Billable);
}
