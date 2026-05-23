using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDockerConnectionComposeProjectPath : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ComposeProjectPath",
                table: "DockerConnections",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ComposeProjectPath",
                table: "DockerConnections");
        }
    }
}
