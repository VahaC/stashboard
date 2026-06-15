using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    /// <remarks>
    /// V7.1 — Compose project directories are discovered per project from the
    /// containers' <c>com.docker.compose.project.working_dir</c> labels; the
    /// V5.2 single <c>ComposeProjectPath</c> per connection is replaced by an
    /// optional host→container prefix mapping. The old column is dropped (not
    /// renamed): its value was the in-container path of ONE project and would
    /// be wrong as a host-side prefix — operators reconfigure the mapping once
    /// in the connection settings.
    /// </remarks>
    public partial class ComposePathMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ComposeProjectPath",
                table: "DockerConnections");

            migrationBuilder.AddColumn<string>(
                name: "ComposePathHostPrefix",
                table: "DockerConnections",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ComposePathContainerPrefix",
                table: "DockerConnections",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ComposePathContainerPrefix",
                table: "DockerConnections");

            migrationBuilder.DropColumn(
                name: "ComposePathHostPrefix",
                table: "DockerConnections");

            migrationBuilder.AddColumn<string>(
                name: "ComposeProjectPath",
                table: "DockerConnections",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }
    }
}
