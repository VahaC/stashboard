using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class ConvertDashboardSortModeToEnum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // V10.6 — DashboardSortMode is now an enum stored by name. Existing rows hold the
            // old lowercase wire form ('name'/'category'/'custom'); rewrite them to the enum
            // member names EF parses on read. Anything unexpected falls back to 'Name'.
            migrationBuilder.Sql("UPDATE Users SET DashboardSortMode = 'Category' WHERE DashboardSortMode = 'category';");
            migrationBuilder.Sql("UPDATE Users SET DashboardSortMode = 'Custom' WHERE DashboardSortMode = 'custom';");
            migrationBuilder.Sql("UPDATE Users SET DashboardSortMode = 'Name' WHERE DashboardSortMode NOT IN ('Category', 'Custom');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Users SET DashboardSortMode = 'category' WHERE DashboardSortMode = 'Category';");
            migrationBuilder.Sql("UPDATE Users SET DashboardSortMode = 'custom' WHERE DashboardSortMode = 'Custom';");
            migrationBuilder.Sql("UPDATE Users SET DashboardSortMode = 'name' WHERE DashboardSortMode NOT IN ('category', 'custom');");
        }
    }
}
