using BusinessOS.Modules.Budgeting.Domain;
using Microsoft.EntityFrameworkCore;

namespace BusinessOS.Modules.Budgeting.Infrastructure.Persistence;

public sealed class BudgetingDbContext(DbContextOptions<BudgetingDbContext> options) : DbContext(options)
{
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<BudgetVersion> BudgetVersions => Set<BudgetVersion>();
    public DbSet<BudgetLine> BudgetLines => Set<BudgetLine>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        var budget = b.Entity<Budget>(); budget.ToTable("budgets"); budget.HasKey(x => x.Id); budget.Property(x => x.Id).HasConversion(x => x.Value, x => new(x)).HasColumnName("id"); budget.Property(x => x.ProjectId).HasConversion(x => x.Value, x => new(x)).HasColumnName("business_project_id"); budget.Property(x => x.Name).HasMaxLength(256).HasColumnName("name"); budget.Property(x => x.NormalizedName).HasMaxLength(256).HasColumnName("normalized_name"); budget.Property(x => x.Status).HasConversion<string>().HasColumnName("status"); budget.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc"); budget.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc"); budget.Property(x => x.Version).IsConcurrencyToken().HasColumnName("version"); budget.Property(x => x.ArchivedAtUtc).HasColumnName("archived_at_utc"); budget.HasIndex(x => x.ProjectId); budget.HasIndex(x => new { x.ProjectId, x.NormalizedName }).IsUnique().HasFilter("archived_at_utc IS NULL");
        var version = b.Entity<BudgetVersion>(); version.ToTable("budget_versions"); version.HasKey(x => x.Id); version.Property(x => x.Id).HasConversion(x => x.Value, x => new(x)).HasColumnName("id"); version.Property(x => x.BudgetId).HasConversion(x => x.Value, x => new(x)).HasColumnName("budget_id"); version.Property(x => x.Number).HasColumnName("number"); version.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc"); version.Property(x => x.Note).HasMaxLength(1000).HasColumnName("note"); version.HasIndex(x => x.BudgetId); version.HasIndex(x => new { x.BudgetId, x.Number }).IsUnique(); version.HasOne<Budget>().WithMany().HasForeignKey(x => x.BudgetId).OnDelete(DeleteBehavior.Cascade);
        var line = b.Entity<BudgetLine>(); line.ToTable("budget_lines"); line.HasKey(x => x.Id); line.Property(x => x.Id).HasColumnName("id"); line.Property(x => x.VersionId).HasConversion(x => x.Value, x => new(x)).HasColumnName("budget_version_id"); line.Property(x => x.Kind).HasConversion<string>().HasColumnName("kind"); line.Property(x => x.Name).HasMaxLength(256).HasColumnName("name"); line.ComplexProperty(x => x.Amount, money => { money.Property(x => x.Amount).HasColumnType("TEXT").HasColumnName("amount"); money.Property(x => x.Currency).HasConversion(x => x.Value, x => new(x)).HasMaxLength(3).HasColumnName("currency"); }); line.Property(x => x.SortOrder).HasColumnName("sort_order"); line.Property(x => x.Note).HasMaxLength(1000).HasColumnName("note"); line.HasIndex(x => x.VersionId); line.HasOne<BudgetVersion>().WithMany().HasForeignKey(x => x.VersionId).OnDelete(DeleteBehavior.Cascade);
    }
}
