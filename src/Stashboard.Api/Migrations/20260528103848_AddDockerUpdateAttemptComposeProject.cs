using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDockerUpdateAttemptComposeProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ComposeProject",
                table: "DockerUpdateAttempts",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentAttemptId",
                table: "DockerUpdateAttempts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DockerUpdateAttempts_ParentAttemptId",
                table: "DockerUpdateAttempts",
                column: "ParentAttemptId");

            migrationBuilder.AddForeignKey(
                name: "FK_DockerUpdateAttempts_DockerUpdateAttempts_ParentAttemptId",
                table: "DockerUpdateAttempts",
                column: "ParentAttemptId",
                principalTable: "DockerUpdateAttempts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DockerUpdateAttempts_DockerUpdateAttempts_ParentAttemptId",
                table: "DockerUpdateAttempts");

            migrationBuilder.DropIndex(
                name: "IX_DockerUpdateAttempts_ParentAttemptId",
                table: "DockerUpdateAttempts");

            migrationBuilder.DropColumn(
                name: "ComposeProject",
                table: "DockerUpdateAttempts");

            migrationBuilder.DropColumn(
                name: "ParentAttemptId",
                table: "DockerUpdateAttempts");
        }
    }
}
