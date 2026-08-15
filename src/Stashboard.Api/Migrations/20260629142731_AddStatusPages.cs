using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddStatusPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StatusPages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    IsPublished = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusPages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusPages_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatusPageItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    StatusPageId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WebResourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusPageItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusPageItems_StatusPages_StatusPageId",
                        column: x => x.StatusPageId,
                        principalTable: "StatusPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StatusPageItems_WebResources_WebResourceId",
                        column: x => x.WebResourceId,
                        principalTable: "WebResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StatusPageItems_StatusPageId_WebResourceId",
                table: "StatusPageItems",
                columns: new[] { "StatusPageId", "WebResourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatusPageItems_WebResourceId",
                table: "StatusPageItems",
                column: "WebResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_StatusPages_Slug",
                table: "StatusPages",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatusPages_UserId_Title",
                table: "StatusPages",
                columns: new[] { "UserId", "Title" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StatusPageItems");

            migrationBuilder.DropTable(
                name: "StatusPages");
        }
    }
}
