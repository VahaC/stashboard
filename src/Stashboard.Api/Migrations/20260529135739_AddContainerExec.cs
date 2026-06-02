using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddContainerExec : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowExec",
                table: "DockerConnections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ContainerExecSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContainerExecSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DockerExecSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InitiatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DockerConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ConnectionName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ContainerName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Command = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("PK_DockerExecSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DockerExecSessions_DockerConnections_DockerConnectionId",
                        column: x => x.DockerConnectionId,
                        principalTable: "DockerConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DockerExecSessions_Users_InitiatedByUserId",
                        column: x => x.InitiatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DockerExecSessions_DockerConnectionId_StartedUtc",
                table: "DockerExecSessions",
                columns: new[] { "DockerConnectionId", "StartedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DockerExecSessions_InitiatedByUserId_StartedUtc",
                table: "DockerExecSessions",
                columns: new[] { "InitiatedByUserId", "StartedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContainerExecSettings");

            migrationBuilder.DropTable(
                name: "DockerExecSessions");

            migrationBuilder.DropColumn(
                name: "AllowExec",
                table: "DockerConnections");
        }
    }
}
