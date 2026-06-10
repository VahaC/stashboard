using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProxmoxNodeAlerting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProxmoxNodeAlertSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProxmoxConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CategoryMask = table.Column<int>(type: "INTEGER", nullable: false),
                    CpuWarn = table.Column<int>(type: "INTEGER", nullable: true),
                    CpuCrit = table.Column<int>(type: "INTEGER", nullable: true),
                    MemWarn = table.Column<int>(type: "INTEGER", nullable: true),
                    MemCrit = table.Column<int>(type: "INTEGER", nullable: true),
                    StorageWarn = table.Column<int>(type: "INTEGER", nullable: true),
                    StorageCrit = table.Column<int>(type: "INTEGER", nullable: true),
                    TempWarn = table.Column<int>(type: "INTEGER", nullable: true),
                    TempCrit = table.Column<int>(type: "INTEGER", nullable: true),
                    LastNotifiedSignature = table.Column<string>(type: "TEXT", nullable: true),
                    LastTelegramNotifiedSignature = table.Column<string>(type: "TEXT", nullable: true),
                    LastNotificationSentUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxmoxNodeAlertSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProxmoxNodeAlertSettings_ProxmoxConnections_ProxmoxConnectionId",
                        column: x => x.ProxmoxConnectionId,
                        principalTable: "ProxmoxConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProxmoxNodeAlertStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProxmoxConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Category = table.Column<int>(type: "INTEGER", nullable: false),
                    ActiveLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    PendingLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    PendingCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Metric = table.Column<string>(type: "TEXT", nullable: true),
                    Value = table.Column<double>(type: "REAL", nullable: true),
                    Threshold = table.Column<double>(type: "REAL", nullable: true),
                    NicCounterBaseline = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxmoxNodeAlertStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProxmoxNodeAlertStates_ProxmoxConnections_ProxmoxConnectionId",
                        column: x => x.ProxmoxConnectionId,
                        principalTable: "ProxmoxConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProxmoxNodeAlertSettings_ProxmoxConnectionId",
                table: "ProxmoxNodeAlertSettings",
                column: "ProxmoxConnectionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProxmoxNodeAlertStates_ProxmoxConnectionId_Category",
                table: "ProxmoxNodeAlertStates",
                columns: new[] { "ProxmoxConnectionId", "Category" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProxmoxNodeAlertSettings");

            migrationBuilder.DropTable(
                name: "ProxmoxNodeAlertStates");
        }
    }
}
