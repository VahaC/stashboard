using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProxmoxGuestDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CpuCores",
                table: "ProxmoxGuests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DiskBytes",
                table: "ProxmoxGuests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IpAddress",
                table: "ProxmoxGuests",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MemoryBytes",
                table: "ProxmoxGuests",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "ProxmoxGuests",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UptimeSeconds",
                table: "ProxmoxGuests",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CpuCores",
                table: "ProxmoxGuests");

            migrationBuilder.DropColumn(
                name: "DiskBytes",
                table: "ProxmoxGuests");

            migrationBuilder.DropColumn(
                name: "IpAddress",
                table: "ProxmoxGuests");

            migrationBuilder.DropColumn(
                name: "MemoryBytes",
                table: "ProxmoxGuests");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "ProxmoxGuests");

            migrationBuilder.DropColumn(
                name: "UptimeSeconds",
                table: "ProxmoxGuests");
        }
    }
}
