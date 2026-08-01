using BusinessOS.Modules.Companies.Infrastructure.Persistence;
using BusinessOS.Modules.Companies.Application;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BusinessOS.IntegrationTests;

public sealed class CompaniesStoreErrorTranslationTests
{
    [Fact]
    public void Active_tax_id_unique_constraint_is_recognized_by_columns()
    {
        var exception = UpdateException("SQLite Error 19: 'UNIQUE constraint failed: companies.organization_id, companies.tax_identification_number'.", 2067);
        CompaniesStore.IsActiveTaxIdConflict(exception).Should().BeTrue();
    }

    [Fact]
    public void Index_name_is_recognized_only_with_unique_extended_code()
    {
        CompaniesStore.IsActiveTaxIdConflict(UpdateException("ux_companies_organization_tax_id_active", 2067)).Should().BeTrue();
        CompaniesStore.IsActiveTaxIdConflict(UpdateException("ux_companies_organization_tax_id_active", 1299)).Should().BeFalse();
    }

    [Fact]
    public void Other_constraint_and_plain_update_errors_are_not_tax_id_duplicates()
    {
        CompaniesStore.IsActiveTaxIdConflict(UpdateException("NOT NULL constraint failed: companies.legal_name", 1299)).Should().BeFalse();
        CompaniesStore.IsActiveTaxIdConflict(new DbUpdateException("ordinary failure")).Should().BeFalse();
    }

    [Fact]
    public void Save_translation_returns_only_expected_statuses_and_preserves_unexpected_exception()
    {
        CompaniesStore.TranslateSaveException(new DbUpdateConcurrencyException("race")).Should().Be(CompaniesSaveStatus.ConcurrencyConflict);
        CompaniesStore.TranslateSaveException(UpdateException("UNIQUE constraint failed: companies.organization_id, companies.tax_identification_number", 2067))
            .Should().Be(CompaniesSaveStatus.DuplicateTaxIdentificationNumber);

        var constraint = UpdateException("NOT NULL constraint failed: companies.legal_name", 1299);
        var translatedConstraint = Assert.Throws<CompaniesPersistenceException>(() => CompaniesStore.TranslateSaveException(constraint));
        translatedConstraint.InnerException.Should().BeSameAs(constraint);
        var ordinary = new DbUpdateException("database file /secret/businessos.db; SQL INSERT");
        var translatedOrdinary = Assert.Throws<CompaniesPersistenceException>(() => CompaniesStore.TranslateSaveException(ordinary));
        translatedOrdinary.InnerException.Should().BeSameAs(ordinary);
    }

    private static DbUpdateException UpdateException(string message, int extendedCode) =>
        new("save failed", new SqliteException(message, 19, extendedCode));
}
