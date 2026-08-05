using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAppriseNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastAppriseNotifiedSignature",
                table: "ProxmoxNodeAlertSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AppriseNotificationsEnabled",
                table: "ProxmoxConnections",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LastAppriseNotifiedSignature",
                table: "ProxmoxConnections",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AppriseNotificationsEnabled",
                table: "DockerWatches",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LastAppriseNotifiedDigest",
                table: "DockerWatches",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppriseSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    UrlsEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppriseSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppriseSettings");

            migrationBuilder.DropColumn(
                name: "LastAppriseNotifiedSignature",
                table: "ProxmoxNodeAlertSettings");

            migrationBuilder.DropColumn(
                name: "AppriseNotificationsEnabled",
                table: "ProxmoxConnections");

            migrationBuilder.DropColumn(
                name: "LastAppriseNotifiedSignature",
                table: "ProxmoxConnections");

            migrationBuilder.DropColumn(
                name: "AppriseNotificationsEnabled",
                table: "DockerWatches");

            migrationBuilder.DropColumn(
                name: "LastAppriseNotifiedDigest",
                table: "DockerWatches");
        }
    }
}
