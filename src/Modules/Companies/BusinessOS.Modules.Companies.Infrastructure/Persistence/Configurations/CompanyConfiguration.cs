using BusinessOS.BuildingBlocks.Domain.Ids;
using BusinessOS.BuildingBlocks.Domain.Primitives;
using BusinessOS.Modules.Companies.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BusinessOS.Modules.Companies.Infrastructure.Persistence.Configurations;

public sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        var companyId = new ValueConverter<CompanyId, Guid>(id => id.Value, value => new CompanyId(value));
        var organizationId = new ValueConverter<OrganizationId, Guid>(id => id.Value, value => new OrganizationId(value));
        var userId = new ValueConverter<UserId, Guid>(id => id.Value, value => new UserId(value));
        var currency = new ValueConverter<CurrencyCode, string>(code => code.Value, value => new CurrencyCode(value));
        var tax = new ValueConverter<TaxIdentificationNumber, string?>(number => number.Value, value => new TaxIdentificationNumber(value));
        var version = new ValueConverter<EntityVersion, long>(entityVersion => entityVersion.Value, value => new EntityVersion(value));

        builder.ToTable("companies");
        builder.HasKey(company => company.Id);
        builder.Property(company => company.Id).HasColumnName("id").HasConversion(companyId).ValueGeneratedNever();
        builder.Property(company => company.OrganizationId).HasColumnName("organization_id").HasConversion(organizationId).IsRequired();
        builder.Property(company => company.LegalName).HasColumnName("legal_name").HasMaxLength(256).IsRequired();
        builder.Property(company => company.DisplayName).HasColumnName("display_name").HasMaxLength(256).IsRequired();
        builder.Property(company => company.TaxIdentificationNumber).HasColumnName("tax_identification_number").HasMaxLength(64).HasConversion(tax).IsRequired(false);
        builder.Property(company => company.CountryCode).HasColumnName("country_code").HasMaxLength(2).IsRequired();
        builder.Property(company => company.BaseCurrency).HasColumnName("base_currency").HasMaxLength(3).HasConversion(currency).IsRequired();
        builder.Property(company => company.DefaultTimeZone).HasColumnName("default_time_zone").HasMaxLength(128).IsRequired();
        builder.Property(company => company.Status).HasColumnName("status").HasMaxLength(32).HasConversion<string>().IsRequired();
        builder.Property(company => company.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(company => company.UpdatedAt).HasColumnName("updated_at").IsRequired();
        builder.Property(company => company.CreatedBy).HasColumnName("created_by").HasConversion(userId).IsRequired();
        builder.Property(company => company.UpdatedBy).HasColumnName("updated_by").HasConversion(userId).IsRequired();
        builder.Property(company => company.Version).HasColumnName("version").HasConversion(version).IsConcurrencyToken().IsRequired();
        builder.Property(company => company.IsDeleted).HasColumnName("is_deleted").IsRequired();
        builder.HasQueryFilter(company => !company.IsDeleted);
        builder.HasIndex(company => company.OrganizationId).HasDatabaseName("ix_companies_organization_id");
        builder.HasIndex(company => company.Status).HasDatabaseName("ix_companies_status");
        builder.HasIndex(company => company.IsDeleted).HasDatabaseName("ix_companies_is_deleted");
        builder.HasIndex(company => new { company.OrganizationId, company.IsDeleted }).HasDatabaseName("ix_companies_organization_id_is_deleted");
        builder.HasIndex(company => new { company.OrganizationId, company.TaxIdentificationNumber })
            .IsUnique()
            .HasFilter("is_deleted = 0 AND tax_identification_number IS NOT NULL")
            .HasDatabaseName("ux_companies_organization_tax_id_active");
    }
}
