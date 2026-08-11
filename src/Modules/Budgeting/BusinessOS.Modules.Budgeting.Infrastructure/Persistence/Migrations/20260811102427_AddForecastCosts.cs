using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BusinessOS.Modules.Budgeting.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddForecastCosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "forecast_costs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    business_project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    kind = table.Column<string>(type: "TEXT", nullable: false),
                    name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    expected_on = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    version = table.Column<long>(type: "INTEGER", nullable: false),
                    archived_at_utc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_forecast_costs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_forecast_costs_business_project_id",
                table: "forecast_costs",
                column: "business_project_id");

            migrationBuilder.CreateIndex(
                name: "IX_forecast_costs_business_project_id_expected_on",
                table: "forecast_costs",
                columns: new[] { "business_project_id", "expected_on" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "forecast_costs");
        }
    }
}
