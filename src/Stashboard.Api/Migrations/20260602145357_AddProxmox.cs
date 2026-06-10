using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProxmox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProxmoxConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ApiBaseUrl = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    NodeName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ApiTokenId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ApiTokenSecretEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    SkipTlsVerify = table.Column<bool>(type: "INTEGER", nullable: false),
                    SshHost = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SshPort = table.Column<int>(type: "INTEGER", nullable: true),
                    SshUsername = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SshPrivateKeyEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    SshPrivateKeyPassphraseEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdateNotificationsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TelegramNotificationsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScheduleType = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckEveryHours = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckAtTime = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    CheckOnDayOfWeek = table.Column<int>(type: "INTEGER", nullable: true),
                    LastCheckedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    LastNotificationSentUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastNotifiedSignature = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LastTelegramNotifiedSignature = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxmoxConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProxmoxConnections_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProxmoxGuests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProxmoxConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VmId = table.Column<int>(type: "INTEGER", nullable: false),
                    GuestType = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsRunning = table.Column<bool>(type: "INTEGER", nullable: false),
                    PendingUpdates = table.Column<int>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    LastCheckedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxmoxGuests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProxmoxGuests_ProxmoxConnections_ProxmoxConnectionId",
                        column: x => x.ProxmoxConnectionId,
                        principalTable: "ProxmoxConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProxmoxConnections_UserId_Enabled_LastCheckedUtc",
                table: "ProxmoxConnections",
                columns: new[] { "UserId", "Enabled", "LastCheckedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ProxmoxConnections_UserId_Name",
                table: "ProxmoxConnections",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProxmoxGuests_ProxmoxConnectionId_VmId",
                table: "ProxmoxGuests",
                columns: new[] { "ProxmoxConnectionId", "VmId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProxmoxGuests");

            migrationBuilder.DropTable(
                name: "ProxmoxConnections");
        }
    }
}
