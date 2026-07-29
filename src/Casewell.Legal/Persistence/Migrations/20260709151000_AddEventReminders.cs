using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cortex.Modules.Legal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEventReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                schema: "legal",
                table: "matter_events",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReminderStage",
                schema: "legal",
                table: "matter_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                schema: "legal",
                table: "matter_events");

            migrationBuilder.DropColumn(
                name: "ReminderStage",
                schema: "legal",
                table: "matter_events");
        }
    }
}
