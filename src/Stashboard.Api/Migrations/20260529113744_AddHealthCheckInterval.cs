using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddHealthCheckInterval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default 60 (the standard scan interval) so the settings row created
            // before this column existed keeps a sane cadence after the upgrade,
            // rather than back-filling to 0 (which the loop would floor to 10 s).
            migrationBuilder.AddColumn<int>(
                name: "IntervalSeconds",
                table: "HealthCheckSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 60);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IntervalSeconds",
                table: "HealthCheckSettings");
        }
    }
}
