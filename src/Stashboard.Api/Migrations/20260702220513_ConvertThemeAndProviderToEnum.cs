using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class ConvertThemeAndProviderToEnum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Theme + email Provider are now enums stored by name. Rewrite the old lowercase
            // theme values ('system'/'light'/'dark') to the enum member names EF parses on read;
            // anything unexpected falls back to 'System'. Provider rows already hold 'Smtp'/'LogOnly'
            // (matching the member names) but are normalised defensively too.
            migrationBuilder.Sql("UPDATE Users SET Theme = 'Light' WHERE Theme = 'light';");
            migrationBuilder.Sql("UPDATE Users SET Theme = 'Dark' WHERE Theme = 'dark';");
            migrationBuilder.Sql("UPDATE Users SET Theme = 'System' WHERE Theme NOT IN ('Light', 'Dark');");
            migrationBuilder.Sql("UPDATE EmailSettings SET Provider = 'LogOnly' WHERE Provider NOT IN ('Smtp');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Users SET Theme = 'light' WHERE Theme = 'Light';");
            migrationBuilder.Sql("UPDATE Users SET Theme = 'dark' WHERE Theme = 'Dark';");
            migrationBuilder.Sql("UPDATE Users SET Theme = 'system' WHERE Theme NOT IN ('light', 'dark');");
        }
    }
}
