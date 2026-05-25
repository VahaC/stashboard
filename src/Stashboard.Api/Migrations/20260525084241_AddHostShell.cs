using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddHostShell : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowHostShell",
                table: "DockerConnections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "HostShellSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InitiatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DockerConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ConnectionName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SshHost = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SshUsername = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BytesFromClient = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesToClient = table.Column<long>(type: "INTEGER", nullable: false),
                    EndReason = table.Column<int>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HostShellSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HostShellSessions_DockerConnections_DockerConnectionId",
                        column: x => x.DockerConnectionId,
                        principalTable: "DockerConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_HostShellSessions_Users_InitiatedByUserId",
                        column: x => x.InitiatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HostShellSessions_DockerConnectionId_StartedUtc",
                table: "HostShellSessions",
                columns: new[] { "DockerConnectionId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HostShellSessions_InitiatedByUserId_StartedUtc",
                table: "HostShellSessions",
                columns: new[] { "InitiatedByUserId", "StartedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HostShellSessions");

            migrationBuilder.DropColumn(
                name: "AllowHostShell",
                table: "DockerConnections");
        }
    }
}
