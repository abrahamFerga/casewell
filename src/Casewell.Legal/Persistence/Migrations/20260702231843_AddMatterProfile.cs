using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cortex.Modules.Legal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMatterProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClosedAt",
                schema: "legal",
                table: "matters",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatterNumber",
                schema: "legal",
                table: "matters",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PracticeArea",
                schema: "legal",
                table: "matters",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_matters_TenantId_MatterNumber",
                schema: "legal",
                table: "matters",
                columns: new[] { "TenantId", "MatterNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_matters_TenantId_MatterNumber",
                schema: "legal",
                table: "matters");

            migrationBuilder.DropColumn(
                name: "ClosedAt",
                schema: "legal",
                table: "matters");

            migrationBuilder.DropColumn(
                name: "MatterNumber",
                schema: "legal",
                table: "matters");

            migrationBuilder.DropColumn(
                name: "PracticeArea",
                schema: "legal",
                table: "matters");
        }
    }
}
