using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cortex.Modules.Legal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingInvoicing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActivityCode",
                schema: "legal",
                table: "time_entries",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InvoiceId",
                schema: "legal",
                table: "time_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "Rate",
                schema: "legal",
                table: "time_entries",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "expenses",
                schema: "legal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MatterId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserDisplay = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IncurredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Billable = table.Column<bool>(type: "boolean", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_expenses_matters_MatterId",
                        column: x => x.MatterId,
                        principalSchema: "legal",
                        principalTable: "matters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                schema: "legal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MatterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: true),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: false),
                    PreparedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PreparedByDisplay = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedByDisplay = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Total = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_invoices_matters_MatterId",
                        column: x => x.MatterId,
                        principalSchema: "legal",
                        principalTable: "matters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "running_timers",
                schema: "legal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MatterId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserDisplay = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ActivityCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_running_timers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_running_timers_matters_MatterId",
                        column: x => x.MatterId,
                        principalSchema: "legal",
                        principalTable: "matters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "invoice_lines",
                schema: "legal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: true),
                    Rate = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: true),
                    OccurredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_invoice_lines_invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "legal",
                        principalTable: "invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_time_entries_MatterId_InvoiceId",
                schema: "legal",
                table: "time_entries",
                columns: new[] { "MatterId", "InvoiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_expenses_MatterId",
                schema: "legal",
                table: "expenses",
                column: "MatterId");

            migrationBuilder.CreateIndex(
                name: "IX_expenses_MatterId_InvoiceId",
                schema: "legal",
                table: "expenses",
                columns: new[] { "MatterId", "InvoiceId" });

            migrationBuilder.CreateIndex(
                name: "IX_invoice_lines_InvoiceId",
                schema: "legal",
                table: "invoice_lines",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_MatterId",
                schema: "legal",
                table: "invoices",
                column: "MatterId");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_TenantId_Number",
                schema: "legal",
                table: "invoices",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_running_timers_MatterId",
                schema: "legal",
                table: "running_timers",
                column: "MatterId");

            migrationBuilder.CreateIndex(
                name: "IX_running_timers_TenantId_UserId",
                schema: "legal",
                table: "running_timers",
                columns: new[] { "TenantId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "expenses",
                schema: "legal");

            migrationBuilder.DropTable(
                name: "invoice_lines",
                schema: "legal");

            migrationBuilder.DropTable(
                name: "running_timers",
                schema: "legal");

            migrationBuilder.DropTable(
                name: "invoices",
                schema: "legal");

            migrationBuilder.DropIndex(
                name: "IX_time_entries_MatterId_InvoiceId",
                schema: "legal",
                table: "time_entries");

            migrationBuilder.DropColumn(
                name: "ActivityCode",
                schema: "legal",
                table: "time_entries");

            migrationBuilder.DropColumn(
                name: "InvoiceId",
                schema: "legal",
                table: "time_entries");

            migrationBuilder.DropColumn(
                name: "Rate",
                schema: "legal",
                table: "time_entries");
        }
    }
}
