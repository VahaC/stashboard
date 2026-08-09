using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddHealthCheckHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Seed existing singleton rows with the real defaults (not 0) so an upgraded
            // instance gets a sane 90-day window / 15-minute sample cadence immediately.
            migrationBuilder.AddColumn<int>(
                name: "HistoryRetentionDays",
                table: "HealthCheckSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 90);

            migrationBuilder.AddColumn<int>(
                name: "HistorySampleIntervalMinutes",
                table: "HealthCheckSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.CreateTable(
                name: "HealthCheckEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WebResourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Target = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ResponseTimeMs = table.Column<int>(type: "INTEGER", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HealthCheckEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HealthCheckEvents_WebResources_WebResourceId",
                        column: x => x.WebResourceId,
                        principalTable: "WebResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckEvents_TimestampUtc",
                table: "HealthCheckEvents",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_HealthCheckEvents_WebResourceId_Target_TimestampUtc",
                table: "HealthCheckEvents",
                columns: new[] { "WebResourceId", "Target", "TimestampUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HealthCheckEvents");

            migrationBuilder.DropColumn(
                name: "HistoryRetentionDays",
                table: "HealthCheckSettings");

            migrationBuilder.DropColumn(
                name: "HistorySampleIntervalMinutes",
                table: "HealthCheckSettings");
        }
    }
}
