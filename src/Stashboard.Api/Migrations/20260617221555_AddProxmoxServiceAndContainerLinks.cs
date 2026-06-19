using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddProxmoxServiceAndContainerLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContainerProxmoxLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DockerConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContainerName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ProxmoxConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VmId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContainerProxmoxLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContainerProxmoxLinks_DockerConnections_DockerConnectionId",
                        column: x => x.DockerConnectionId,
                        principalTable: "DockerConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContainerProxmoxLinks_ProxmoxConnections_ProxmoxConnectionId",
                        column: x => x.ProxmoxConnectionId,
                        principalTable: "ProxmoxConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContainerProxmoxLinks_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebResourceProxmoxGuestLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WebResourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProxmoxConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VmId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebResourceProxmoxGuestLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebResourceProxmoxGuestLinks_ProxmoxConnections_ProxmoxConnectionId",
                        column: x => x.ProxmoxConnectionId,
                        principalTable: "ProxmoxConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WebResourceProxmoxGuestLinks_WebResources_WebResourceId",
                        column: x => x.WebResourceId,
                        principalTable: "WebResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContainerProxmoxLinks_DockerConnectionId",
                table: "ContainerProxmoxLinks",
                column: "DockerConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ContainerProxmoxLinks_ProxmoxConnectionId",
                table: "ContainerProxmoxLinks",
                column: "ProxmoxConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_ContainerProxmoxLinks_UserId_DockerConnectionId_ContainerName",
                table: "ContainerProxmoxLinks",
                columns: new[] { "UserId", "DockerConnectionId", "ContainerName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebResourceProxmoxGuestLinks_ProxmoxConnectionId_VmId",
                table: "WebResourceProxmoxGuestLinks",
                columns: new[] { "ProxmoxConnectionId", "VmId" });

            migrationBuilder.CreateIndex(
                name: "IX_WebResourceProxmoxGuestLinks_WebResourceId_ProxmoxConnectionId_VmId",
                table: "WebResourceProxmoxGuestLinks",
                columns: new[] { "WebResourceId", "ProxmoxConnectionId", "VmId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContainerProxmoxLinks");

            migrationBuilder.DropTable(
                name: "WebResourceProxmoxGuestLinks");
        }
    }
}
