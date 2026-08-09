using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BusinessOS.Modules.Budgeting.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialBudgetingPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "budgets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    business_project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    normalized_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    status = table.Column<string>(type: "TEXT", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    archived_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budgets", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "budget_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    budget_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    number = table.Column<int>(type: "INTEGER", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_versions", x => x.id);
                    table.ForeignKey(
                        name: "FK_budget_versions_budgets_budget_id",
                        column: x => x.budget_id,
                        principalTable: "budgets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "budget_lines",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    budget_version_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    sort_order = table.Column<int>(type: "INTEGER", nullable: false),
                    note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_budget_lines_budget_versions_budget_version_id",
                        column: x => x.budget_version_id,
                        principalTable: "budget_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_budget_lines_budget_version_id",
                table: "budget_lines",
                column: "budget_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_budget_versions_budget_id",
                table: "budget_versions",
                column: "budget_id");

            migrationBuilder.CreateIndex(
                name: "IX_budget_versions_budget_id_number",
                table: "budget_versions",
                columns: new[] { "budget_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_budgets_business_project_id",
                table: "budgets",
                column: "business_project_id");

            migrationBuilder.CreateIndex(
                name: "IX_budgets_business_project_id_normalized_name",
                table: "budgets",
                columns: new[] { "business_project_id", "normalized_name" },
                unique: true,
                filter: "archived_at_utc IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "budget_lines");

            migrationBuilder.DropTable(
                name: "budget_versions");

            migrationBuilder.DropTable(
                name: "budgets");
        }
    }
}
