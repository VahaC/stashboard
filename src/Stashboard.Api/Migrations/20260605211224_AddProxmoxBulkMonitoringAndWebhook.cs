using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProxmoxBulkMonitoringAndWebhook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MonitoringSnoozedUntil",
                table: "ProxmoxGuests",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastWebhookReceivedUtc",
                table: "ProxmoxConnections",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookToken",
                table: "ProxmoxConnections",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProxmoxMonitoringAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    InitiatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProxmoxConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ConnectionName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    NodeName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    VmId = table.Column<int>(type: "INTEGER", nullable: false),
                    GuestName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    ChangeType = table.Column<int>(type: "INTEGER", nullable: false),
                    MonitoringEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SnoozedUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Bulk = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChangedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxmoxMonitoringAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProxmoxMonitoringAudits_ProxmoxConnections_ProxmoxConnectionId",
                        column: x => x.ProxmoxConnectionId,
                        principalTable: "ProxmoxConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ProxmoxMonitoringAudits_Users_InitiatedByUserId",
                        column: x => x.InitiatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProxmoxConnections_WebhookToken",
                table: "ProxmoxConnections",
                column: "WebhookToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProxmoxMonitoringAudits_InitiatedByUserId_ChangedUtc",
                table: "ProxmoxMonitoringAudits",
                columns: new[] { "InitiatedByUserId", "ChangedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProxmoxMonitoringAudits_ProxmoxConnectionId_ChangedUtc",
                table: "ProxmoxMonitoringAudits",
                columns: new[] { "ProxmoxConnectionId", "ChangedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProxmoxMonitoringAudits");

            migrationBuilder.DropIndex(
                name: "IX_ProxmoxConnections_WebhookToken",
                table: "ProxmoxConnections");

            migrationBuilder.DropColumn(
                name: "MonitoringSnoozedUntil",
                table: "ProxmoxGuests");

            migrationBuilder.DropColumn(
                name: "LastWebhookReceivedUtc",
                table: "ProxmoxConnections");

            migrationBuilder.DropColumn(
                name: "WebhookToken",
                table: "ProxmoxConnections");
        }
    }
}
