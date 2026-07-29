using Cortex.Core.Multitenancy;
using Cortex.Modules.Sdk;
using Microsoft.EntityFrameworkCore;

namespace Cortex.Modules.Legal.Persistence;

/// <summary>
/// The Legal module's own database — co-located in the platform database under a dedicated
/// <c>legal</c> schema and migrated via the module's <c>MigrateAsync</c> hook. The same global
/// query-filter pattern the platform uses enforces tenant isolation on matters and their documents.
/// <see cref="ModuleDbContext"/> stamps CreatedAt/UpdatedAt on every save — the platform's
/// audit interceptor only rides the platform context, so a module context must bring its own.
/// </summary>
public sealed class LegalDbContext(
    DbContextOptions<LegalDbContext> options,
    ITenantContext tenantContext) : ModuleDbContext(options)
{
    /// <summary>Connection shared with the platform database (separate schema).</summary>
    public const string ConnectionName = "cortex-platform";
    public const string Schema = "legal";

    public DbSet<Matter> Matters => Set<Matter>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<MatterDocument> MatterDocuments => Set<MatterDocument>();
    public DbSet<MatterParty> MatterParties => Set<MatterParty>();
    public DbSet<MatterEvent> MatterEvents => Set<MatterEvent>();
    public DbSet<ConflictAttestation> ConflictAttestations => Set<ConflictAttestation>();
    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();
    public DbSet<MatterTask> MatterTasks => Set<MatterTask>();
    public DbSet<TenantClause> Clauses => Set<TenantClause>();
    public DbSet<DocumentTemplate> DocumentTemplates => Set<DocumentTemplate>();
    public DbSet<PlaybookRule> PlaybookRules => Set<PlaybookRule>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Matter>(b =>
        {
            b.ToTable("matters");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.MatterNumber).HasMaxLength(16);
            b.Property(x => x.ClientName).HasMaxLength(200);
            b.Property(x => x.ClientEmail).HasMaxLength(320);
            b.Property(x => x.PracticeArea).HasMaxLength(100);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            // Docket numbers are unique per tenant; pre-numbering rows stay null (nulls don't collide).
            b.HasIndex(x => new { x.TenantId, x.MatterNumber }).IsUnique();
            b.HasMany(x => x.Documents).WithOne().HasForeignKey(d => d.MatterId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<Client>(b =>
        {
            b.ToTable("clients");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Email).HasMaxLength(320);
            b.Property(x => x.Phone).HasMaxLength(50);
            b.Property(x => x.Organization).HasMaxLength(200);
            b.Property(x => x.Notes).HasMaxLength(1000);
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<MatterDocument>(b =>
        {
            b.ToTable("matter_documents");
            b.HasKey(x => x.Id);
            b.Property(x => x.FileName).HasMaxLength(300).IsRequired();
            b.Property(x => x.Note).HasMaxLength(500);
            b.HasIndex(x => x.MatterId);
            b.HasIndex(x => new { x.MatterId, x.FileId }).IsUnique(); // a file attaches to a matter once
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<TimeEntry>(b =>
        {
            b.ToTable("time_entries");
            b.HasKey(x => x.Id);
            b.Property(x => x.Description).HasMaxLength(1000).IsRequired();
            b.Property(x => x.UserDisplay).HasMaxLength(200);
            b.Property(x => x.Hours).HasPrecision(5, 2);
            b.HasIndex(x => x.MatterId);
            b.HasIndex(x => new { x.TenantId, x.WorkedOn });
            b.HasOne<Matter>().WithMany().HasForeignKey(x => x.MatterId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<MatterTask>(b =>
        {
            b.ToTable("matter_tasks");
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(300).IsRequired();
            b.Property(x => x.AssignedTo).HasMaxLength(200);
            b.Property(x => x.Notes).HasMaxLength(1000);
            b.HasIndex(x => x.MatterId);
            b.HasIndex(x => new { x.TenantId, x.CompletedAt });
            b.HasOne<Matter>().WithMany().HasForeignKey(x => x.MatterId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<MatterParty>(b =>
        {
            b.ToTable("matter_parties");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Role).HasMaxLength(16).IsRequired();
            b.HasIndex(x => x.MatterId);
            b.HasOne<Matter>().WithMany().HasForeignKey(x => x.MatterId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<MatterEvent>(b =>
        {
            b.ToTable("matter_events");
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(300).IsRequired();
            b.Property(x => x.Type).HasMaxLength(16).IsRequired();
            b.Property(x => x.Notes).HasMaxLength(1000);
            b.HasIndex(x => new { x.TenantId, x.StartsAt });
            b.HasIndex(x => x.MatterId);
            b.HasOne<Matter>().WithMany().HasForeignKey(x => x.MatterId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<ConflictAttestation>(b =>
        {
            b.ToTable("conflict_attestations");
            b.HasKey(x => x.Id);
            b.Property(x => x.SearchTermsJson).IsRequired();
            b.Property(x => x.DataSnapshotJson).IsRequired();
            b.Property(x => x.PriorAttestationHash).HasMaxLength(64);
            b.Property(x => x.AttestationHash).HasMaxLength(64).IsRequired();
            b.HasIndex(x => new { x.MatterId, x.PerformedAt });
            b.HasOne<Matter>().WithMany().HasForeignKey(x => x.MatterId).OnDelete(DeleteBehavior.Cascade);
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<TenantClause>(b =>
        {
            b.ToTable("clauses");
            b.HasKey(x => x.Id);
            b.Property(x => x.Slug).HasMaxLength(100).IsRequired();
            b.Property(x => x.Title).HasMaxLength(200).IsRequired();
            b.Property(x => x.Category).HasMaxLength(100).IsRequired();
            b.Property(x => x.Summary).HasMaxLength(500).IsRequired();
            b.Property(x => x.Template).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Slug }).IsUnique();
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<DocumentTemplate>(b =>
        {
            b.ToTable("document_templates");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.Title).HasMaxLength(300).IsRequired();
            b.Property(x => x.ClauseSlugsJson).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        modelBuilder.Entity<PlaybookRule>(b =>
        {
            b.ToTable("playbook_rules");
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(200).IsRequired();
            b.Property(x => x.Guidance).HasMaxLength(2000).IsRequired();
            b.Property(x => x.Severity).HasConversion<string>().HasMaxLength(16);
            b.HasIndex(x => x.TenantId);
            b.HasQueryFilter(x => x.TenantId == tenantContext.TenantId);
        });

        base.OnModelCreating(modelBuilder);
    }
}
