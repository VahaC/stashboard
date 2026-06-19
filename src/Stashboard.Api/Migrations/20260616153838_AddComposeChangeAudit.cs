using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddComposeChangeAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ComposeChangeAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DockerConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ConnectionName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ComposeProject = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ChangeType = table.Column<int>(type: "INTEGER", nullable: false),
                    ChangedServices = table.Column<string>(type: "TEXT", nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    InitiatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChangedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ComposeChangeAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ComposeChangeAudits_DockerConnections_DockerConnectionId",
                        column: x => x.DockerConnectionId,
                        principalTable: "DockerConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ComposeChangeAudits_Users_InitiatedByUserId",
                        column: x => x.InitiatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ComposeChangeAudits_DockerConnectionId_ChangedUtc",
                table: "ComposeChangeAudits",
                columns: new[] { "DockerConnectionId", "ChangedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ComposeChangeAudits_InitiatedByUserId_ChangedUtc",
                table: "ComposeChangeAudits",
                columns: new[] { "InitiatedByUserId", "ChangedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ComposeChangeAudits");
        }
    }
}
