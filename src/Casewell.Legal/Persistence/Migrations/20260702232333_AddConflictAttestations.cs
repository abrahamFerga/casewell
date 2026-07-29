using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cortex.Modules.Legal.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConflictAttestations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "conflict_attestations",
                schema: "legal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MatterId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PerformedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SearchTermsJson = table.Column<string>(type: "text", nullable: false),
                    DataSnapshotJson = table.Column<string>(type: "text", nullable: false),
                    PriorAttestationHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    AttestationHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conflict_attestations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_conflict_attestations_matters_MatterId",
                        column: x => x.MatterId,
                        principalSchema: "legal",
                        principalTable: "matters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "matter_parties",
                schema: "legal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MatterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_matter_parties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_matter_parties_matters_MatterId",
                        column: x => x.MatterId,
                        principalSchema: "legal",
                        principalTable: "matters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_conflict_attestations_MatterId_PerformedAt",
                schema: "legal",
                table: "conflict_attestations",
                columns: new[] { "MatterId", "PerformedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_matter_parties_MatterId",
                schema: "legal",
                table: "matter_parties",
                column: "MatterId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conflict_attestations",
                schema: "legal");

            migrationBuilder.DropTable(
                name: "matter_parties",
                schema: "legal");
        }
    }
}
