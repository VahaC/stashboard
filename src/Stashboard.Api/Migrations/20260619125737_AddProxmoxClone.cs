using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProxmoxClone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowClone",
                table: "ProxmoxConnections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ProxmoxCloneAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InitiatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProxmoxConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ConnectionName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    NodeName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    VmId = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxmoxCloneAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProxmoxCloneAudits_ProxmoxConnections_ProxmoxConnectionId",
                        column: x => x.ProxmoxConnectionId,
                        principalTable: "ProxmoxConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProxmoxCloneAudits_Users_InitiatedByUserId",
                        column: x => x.InitiatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProxmoxCloneSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxmoxCloneSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProxmoxCloneAudits_InitiatedByUserId_CreatedAtUtc",
                table: "ProxmoxCloneAudits",
                columns: new[] { "InitiatedByUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProxmoxCloneAudits_ProxmoxConnectionId_VmId_CreatedAtUtc",
                table: "ProxmoxCloneAudits",
                columns: new[] { "ProxmoxConnectionId", "VmId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProxmoxCloneAudits");

            migrationBuilder.DropTable(
                name: "ProxmoxCloneSettings");

            migrationBuilder.DropColumn(
                name: "AllowClone",
                table: "ProxmoxConnections");
        }
    }
}
