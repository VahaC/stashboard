using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOidcSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "LocalLoginDisabled",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "OidcSubject",
                table: "Users",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OidcSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Issuer = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ClientSecretEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    Scopes = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RedirectBaseUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    AllowOidcRegistration = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OidcSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_OidcSubject",
                table: "Users",
                column: "OidcSubject");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OidcSettings");

            migrationBuilder.DropIndex(
                name: "IX_Users_OidcSubject",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LocalLoginDisabled",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "OidcSubject",
                table: "Users");
        }
    }
}
