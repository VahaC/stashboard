using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProxmoxConfigAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProxmoxConfigAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InitiatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProxmoxConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ConnectionName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    NodeName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    VmId = table.Column<int>(type: "INTEGER", nullable: false),
                    GuestKind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxmoxConfigAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProxmoxConfigAudits_ProxmoxConnections_ProxmoxConnectionId",
                        column: x => x.ProxmoxConnectionId,
                        principalTable: "ProxmoxConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProxmoxConfigAudits_Users_InitiatedByUserId",
                        column: x => x.InitiatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProxmoxConfigAudits_InitiatedByUserId_CreatedAtUtc",
                table: "ProxmoxConfigAudits",
                columns: new[] { "InitiatedByUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProxmoxConfigAudits_ProxmoxConnectionId_VmId_CreatedAtUtc",
                table: "ProxmoxConfigAudits",
                columns: new[] { "ProxmoxConnectionId", "VmId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProxmoxConfigAudits");
        }
    }
}
