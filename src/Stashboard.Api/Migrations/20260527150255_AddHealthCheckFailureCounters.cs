using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddHealthCheckFailureCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AdditionalUrlConsecutiveFailures",
                table: "WebResources",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MainUrlConsecutiveFailures",
                table: "WebResources",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdditionalUrlConsecutiveFailures",
                table: "WebResources");

            migrationBuilder.DropColumn(
                name: "MainUrlConsecutiveFailures",
                table: "WebResources");
        }
    }
}
