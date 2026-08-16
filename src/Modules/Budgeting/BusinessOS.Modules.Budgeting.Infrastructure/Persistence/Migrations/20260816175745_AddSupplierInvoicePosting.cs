using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BusinessOS.Modules.Budgeting.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierInvoicePosting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "posted_actual_cost_id",
                table: "supplier_invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "posted_at_utc",
                table: "supplier_invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_invoices_posted_actual_cost_id",
                table: "supplier_invoices",
                column: "posted_actual_cost_id",
                unique: true,
                filter: "posted_actual_cost_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_supplier_invoices_posting_pair",
                table: "supplier_invoices",
                sql: "(posted_actual_cost_id IS NULL AND posted_at_utc IS NULL) OR (posted_actual_cost_id IS NOT NULL AND posted_at_utc IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_supplier_invoices_actual_costs_posted_actual_cost_id",
                table: "supplier_invoices",
                column: "posted_actual_cost_id",
                principalTable: "actual_costs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_supplier_invoices_actual_costs_posted_actual_cost_id",
                table: "supplier_invoices");

            migrationBuilder.DropIndex(
                name: "IX_supplier_invoices_posted_actual_cost_id",
                table: "supplier_invoices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_supplier_invoices_posting_pair",
                table: "supplier_invoices");

            migrationBuilder.DropColumn(
                name: "posted_actual_cost_id",
                table: "supplier_invoices");

            migrationBuilder.DropColumn(
                name: "posted_at_utc",
                table: "supplier_invoices");
        }
    }
}
