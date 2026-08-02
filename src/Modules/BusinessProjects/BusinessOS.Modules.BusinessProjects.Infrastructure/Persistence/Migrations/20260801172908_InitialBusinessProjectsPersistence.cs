using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BusinessOS.Modules.BusinessProjects.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialBusinessProjectsPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "business_projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    company_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false, collation: "NOCASE"),
                    business_type = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    location = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    description = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    planned_start_date = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    planned_opening_date = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    base_currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    created_by = table.Column<Guid>(type: "TEXT", nullable: false),
                    updated_by = table.Column<Guid>(type: "TEXT", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    is_deleted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_business_projects", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_business_projects_company_id",
                table: "business_projects",
                column: "company_id");

            migrationBuilder.CreateIndex(
                name: "ix_business_projects_company_id_is_deleted",
                table: "business_projects",
                columns: new[] { "company_id", "is_deleted" });

            migrationBuilder.CreateIndex(
                name: "ix_business_projects_is_deleted",
                table: "business_projects",
                column: "is_deleted");

            migrationBuilder.CreateIndex(
                name: "ix_business_projects_planned_opening_date",
                table: "business_projects",
                column: "planned_opening_date");

            migrationBuilder.CreateIndex(
                name: "ix_business_projects_status",
                table: "business_projects",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_business_projects_company_name_active",
                table: "business_projects",
                columns: new[] { "company_id", "name" },
                unique: true,
                filter: "is_deleted = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "business_projects");
        }
    }
}
