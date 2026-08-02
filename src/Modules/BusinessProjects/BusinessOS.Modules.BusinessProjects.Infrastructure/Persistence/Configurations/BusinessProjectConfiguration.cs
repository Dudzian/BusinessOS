using System.Globalization;
using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.BusinessProjects.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
namespace BusinessOS.Modules.BusinessProjects.Infrastructure.Persistence.Configurations;

public sealed class BusinessProjectConfiguration : IEntityTypeConfiguration<BusinessProject>
{
    public void Configure(EntityTypeBuilder<BusinessProject> b)
    {
        b.ToTable("business_projects"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").HasConversion(x => x.Value, x => new BusinessProjectId(x)).ValueGeneratedNever();
        b.Property(x => x.CompanyId).HasColumnName("company_id").HasConversion(x => x.Value, x => new CompanyId(x)).IsRequired();
        b.Property(x => x.Name).HasColumnName("name").UseCollation("NOCASE").HasMaxLength(256).IsRequired();
        b.Property(x => x.BusinessType).HasColumnName("business_type").HasMaxLength(128).IsRequired(); b.Property(x => x.Location).HasColumnName("location").HasMaxLength(256).IsRequired(); b.Property(x => x.Description).HasColumnName("description").HasMaxLength(4000).IsRequired();
        b.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32).IsRequired();
        var date = new ValueConverter<DateOnly, string>(x => x.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), x => DateOnly.ParseExact(x, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        b.Property(x => x.PlannedStartDate).HasColumnName("planned_start_date").HasConversion(date).HasMaxLength(10).IsRequired(); b.Property(x => x.PlannedOpeningDate).HasColumnName("planned_opening_date").HasConversion(date).HasMaxLength(10).IsRequired();
        b.Property(x => x.BaseCurrency).HasColumnName("base_currency").HasConversion(x => x.Value, x => new CurrencyCode(x)).HasMaxLength(3).IsRequired(); b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired(); b.Property(x => x.UpdatedAt).HasColumnName("updated_at").IsRequired();
        b.Property(x => x.CreatedBy).HasColumnName("created_by").HasConversion(x => x.Value, x => new UserId(x)).IsRequired(); b.Property(x => x.UpdatedBy).HasColumnName("updated_by").HasConversion(x => x.Value, x => new UserId(x)).IsRequired();
        b.Property(x => x.Version).HasColumnName("version").HasConversion(x => x.Value, x => new EntityVersion(x)).IsConcurrencyToken().IsRequired(); b.Property(x => x.IsDeleted).HasColumnName("is_deleted").IsRequired(); b.HasQueryFilter(x => !x.IsDeleted);
        b.HasIndex(x => x.CompanyId).HasDatabaseName("ix_business_projects_company_id"); b.HasIndex(x => x.Status).HasDatabaseName("ix_business_projects_status"); b.HasIndex(x => x.PlannedOpeningDate).HasDatabaseName("ix_business_projects_planned_opening_date"); b.HasIndex(x => x.IsDeleted).HasDatabaseName("ix_business_projects_is_deleted"); b.HasIndex(x => new { x.CompanyId, x.IsDeleted }).HasDatabaseName("ix_business_projects_company_id_is_deleted"); b.HasIndex(x => new { x.CompanyId, x.Name }).IsUnique().HasFilter("is_deleted = 0").HasDatabaseName("ux_business_projects_company_name_active");
    }
}
