using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMqttDeviceBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceName",
                table: "MqttSettings",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "Stashboard");

            migrationBuilder.AddColumn<string>(
                name: "Manufacturer",
                table: "MqttSettings",
                type: "TEXT",
                maxLength: 64,
                nullable: false,
                defaultValue: "Stashboard");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeviceName",
                table: "MqttSettings");

            migrationBuilder.DropColumn(
                name: "Manufacturer",
                table: "MqttSettings");
        }
    }
}
