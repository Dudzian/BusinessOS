using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BusinessOS.Modules.Companies.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveCompanyTaxIdUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ux_companies_organization_tax_id_active",
                table: "companies",
                columns: new[] { "organization_id", "tax_identification_number" },
                unique: true,
                filter: "is_deleted = 0 AND tax_identification_number IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_companies_organization_tax_id_active",
                table: "companies");
        }
    }
}
