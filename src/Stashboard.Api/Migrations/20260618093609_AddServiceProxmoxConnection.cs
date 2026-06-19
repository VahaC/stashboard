using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceProxmoxConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ProxmoxConnectionId",
                table: "WebResources",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebResources_ProxmoxConnectionId",
                table: "WebResources",
                column: "ProxmoxConnectionId");

            migrationBuilder.AddForeignKey(
                name: "FK_WebResources_ProxmoxConnections_ProxmoxConnectionId",
                table: "WebResources",
                column: "ProxmoxConnectionId",
                principalTable: "ProxmoxConnections",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WebResources_ProxmoxConnections_ProxmoxConnectionId",
                table: "WebResources");

            migrationBuilder.DropIndex(
                name: "IX_WebResources_ProxmoxConnectionId",
                table: "WebResources");

            migrationBuilder.DropColumn(
                name: "ProxmoxConnectionId",
                table: "WebResources");
        }
    }
}
