using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cortex.Modules.Legal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAssistantFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "assistant_feedback",
                schema: "legal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserDisplay = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Helpful = table.Column<bool>(type: "boolean", nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_feedback", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_feedback_ConversationId",
                schema: "legal",
                table: "assistant_feedback",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_feedback_TenantId_MessageId_UserId",
                schema: "legal",
                table: "assistant_feedback",
                columns: new[] { "TenantId", "MessageId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "assistant_feedback",
                schema: "legal");
        }
    }
}
