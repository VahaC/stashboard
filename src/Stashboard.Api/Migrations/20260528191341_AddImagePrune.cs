using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddImagePrune : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default true so existing connections opt in automatically on
            // upgrade — pruning dangling images is safe, and the whole point
            // of V5.5 is to stop disks filling up over months of auto-updates.
            migrationBuilder.AddColumn<bool>(
                name: "AllowImagePrune",
                table: "DockerConnections",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastImagePruneUtc",
                table: "DockerConnections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PruneUnusedImages",
                table: "DockerConnections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "DockerPruneRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DockerConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    InitiatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Trigger = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    IncludedUnused = table.Column<bool>(type: "INTEGER", nullable: false),
                    ImagesDeleted = table.Column<int>(type: "INTEGER", nullable: false),
                    SpaceReclaimedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DockerPruneRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DockerPruneRuns_DockerConnections_DockerConnectionId",
                        column: x => x.DockerConnectionId,
                        principalTable: "DockerConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DockerPruneRuns_Users_InitiatedByUserId",
                        column: x => x.InitiatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ImagePruneSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IntervalHours = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImagePruneSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DockerPruneRuns_DockerConnectionId_StartedUtc",
                table: "DockerPruneRuns",
                columns: new[] { "DockerConnectionId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DockerPruneRuns_InitiatedByUserId",
                table: "DockerPruneRuns",
                column: "InitiatedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DockerPruneRuns");

            migrationBuilder.DropTable(
                name: "ImagePruneSettings");

            migrationBuilder.DropColumn(
                name: "AllowImagePrune",
                table: "DockerConnections");

            migrationBuilder.DropColumn(
                name: "LastImagePruneUtc",
                table: "DockerConnections");

            migrationBuilder.DropColumn(
                name: "PruneUnusedImages",
                table: "DockerConnections");
        }
    }
}
