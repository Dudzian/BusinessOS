using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BusinessOS.Modules.Budgeting.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "supplier_invoices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "TEXT", nullable: false),
                    business_project_id = table.Column<Guid>(type: "TEXT", nullable: false),
                    supplier_name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    supplier_key = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    invoice_number = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    invoice_number_key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    invoice_date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    due_date = table.Column<DateOnly>(type: "TEXT", nullable: false),
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
                    table.PrimaryKey("PK_supplier_invoices", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_invoices_business_project_id",
                table: "supplier_invoices",
                column: "business_project_id");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_invoices_business_project_id_due_date",
                table: "supplier_invoices",
                columns: new[] { "business_project_id", "due_date" });

            migrationBuilder.CreateIndex(
                name: "IX_supplier_invoices_business_project_id_supplier_key_invoice_number_key",
                table: "supplier_invoices",
                columns: new[] { "business_project_id", "supplier_key", "invoice_number_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "supplier_invoices");
        }
    }
}
