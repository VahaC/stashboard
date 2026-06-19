using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProxmoxGuestIcons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OsType",
                table: "ProxmoxGuests",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ProxmoxGuestIcons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProxmoxConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VmId = table.Column<int>(type: "INTEGER", nullable: false),
                    IconSource = table.Column<int>(type: "INTEGER", nullable: false),
                    LogoBase64 = table.Column<string>(type: "TEXT", nullable: true),
                    CustomLogoPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProxmoxGuestIcons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProxmoxGuestIcons_ProxmoxConnections_ProxmoxConnectionId",
                        column: x => x.ProxmoxConnectionId,
                        principalTable: "ProxmoxConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProxmoxGuestIcons_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProxmoxGuestIcons_ProxmoxConnectionId",
                table: "ProxmoxGuestIcons",
                column: "ProxmoxConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ProxmoxGuestIcons_UserId_ProxmoxConnectionId_VmId",
                table: "ProxmoxGuestIcons",
                columns: new[] { "UserId", "ProxmoxConnectionId", "VmId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProxmoxGuestIcons");

            migrationBuilder.DropColumn(
                name: "OsType",
                table: "ProxmoxGuests");
        }
    }
}
