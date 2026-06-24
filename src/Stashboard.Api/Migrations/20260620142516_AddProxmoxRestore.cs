using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProxmoxRestore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AllowRestore",
                table: "ProxmoxConnections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ProxmoxRestoreAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InitiatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProxmoxConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ConnectionName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    NodeName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    VmId = table.Column<int>(type: "INTEGER", nullable: false),
                    BackupVolid = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Overwrote = table.Column<bool>(type: "INTEGER", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxmoxRestoreAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProxmoxRestoreAudits_ProxmoxConnections_ProxmoxConnectionId",
                        column: x => x.ProxmoxConnectionId,
                        principalTable: "ProxmoxConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProxmoxRestoreAudits_Users_InitiatedByUserId",
                        column: x => x.InitiatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProxmoxRestoreSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxmoxRestoreSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProxmoxRestoreAudits_InitiatedByUserId_CreatedAtUtc",
                table: "ProxmoxRestoreAudits",
                columns: new[] { "InitiatedByUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProxmoxRestoreAudits_ProxmoxConnectionId_CreatedAtUtc",
                table: "ProxmoxRestoreAudits",
                columns: new[] { "ProxmoxConnectionId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProxmoxRestoreAudits");

            migrationBuilder.DropTable(
                name: "ProxmoxRestoreSettings");

            migrationBuilder.DropColumn(
                name: "AllowRestore",
                table: "ProxmoxConnections");
        }
    }
}
