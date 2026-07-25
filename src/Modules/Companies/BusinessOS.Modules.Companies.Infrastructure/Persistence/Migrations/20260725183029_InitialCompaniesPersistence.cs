using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BusinessOS.Modules.Companies.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCompaniesPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "companies",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    organization_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    legal_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    display_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    tax_identification_number = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    country_code = table.Column<string>(type: "TEXT", maxLength: 2, nullable: false),
                    base_currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    default_time_zone = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_companies", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_companies_is_deleted",
                table: "companies",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_companies_organization_id",
                table: "companies",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_companies_organization_id_is_deleted",
                table: "companies",
                columns: new[] { "organization_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "ix_companies_status",
                table: "companies",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "companies");
        }
    }
}
