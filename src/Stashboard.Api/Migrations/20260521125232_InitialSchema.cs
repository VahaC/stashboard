using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Stashboard.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "TEXT", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LockoutEndUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailedAccessCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastLoginUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Theme = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DashboardSortMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DashboardGroupByCategory = table.Column<bool>(type: "INTEGER", nullable: false),
                    TelegramBotToken = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    TelegramChatId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TelegramNotificationsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    EmailConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    EmailConfirmationTokenHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    EmailConfirmationTokenExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PasswordResetTokenHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PasswordResetTokenExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PendingEmail = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PendingEmailTokenHash = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PendingEmailTokenExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Categories_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DockerConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    HostType = table.Column<int>(type: "INTEGER", nullable: false),
                    HostUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TlsCaCertEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    TlsClientCertEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    TlsClientKeyEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    SshHost = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SshPort = table.Column<int>(type: "INTEGER", nullable: true),
                    SshUsername = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SshPrivateKeyEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    SshPrivateKeyPassphraseEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    SshRemoteSocketPath = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DockerConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DockerConnections_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FamilyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SessionExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RevokedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedReason = table.Column<int>(type: "INTEGER", nullable: true),
                    ReplacedById = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefreshTokens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tags_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WebResources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    MainUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    MainUrlHealthCheckEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AdditionalUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AdditionalUrlHealthCheckEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    OfflineNotificationsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    HealthCheckUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    HealthCheckMethod = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpectedStatusRange = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CategoryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LogoSource = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomLogoPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CurrentStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    LastCheckedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastResponseTimeMs = table.Column<int>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    AdditionalUrlStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    AdditionalUrlLastResponseTimeMs = table.Column<int>(type: "INTEGER", nullable: true),
                    AdditionalUrlLastError = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DockerConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebResources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WebResources_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WebResources_DockerConnections_DockerConnectionId",
                        column: x => x.DockerConnectionId,
                        principalTable: "DockerConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WebResources_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WebResourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    EncryptedValue = table.Column<string>(type: "TEXT", nullable: false),
                    IsSecret = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Credentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Credentials_WebResources_WebResourceId",
                        column: x => x.WebResourceId,
                        principalTable: "WebResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DockerWatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DockerConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WebResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ImageReference = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    RegistryHost = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Repository = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Tag = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ContainerName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RegistryUsernameEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    RegistryPasswordEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    GitHubPatEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    RegistryAuthType = table.Column<int>(type: "INTEGER", nullable: false),
                    AwsAccessKeyIdEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    AwsSecretAccessKeyEncrypted = table.Column<string>(type: "TEXT", nullable: true),
                    AwsRegion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    UpdateNotificationsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TelegramNotificationsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScheduleType = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckEveryHours = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckAtTime = table.Column<TimeOnly>(type: "TEXT", nullable: true),
                    CheckOnDayOfWeek = table.Column<int>(type: "INTEGER", nullable: true),
                    TagPatternFilter = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    UpdateStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrentDigest = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LatestDigest = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CurrentVersionTag = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LatestVersionTag = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LatestReleaseUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LatestReleaseBody = table.Column<string>(type: "TEXT", nullable: true),
                    LastCheckedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUpdateDetectedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastNotificationSentUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastNotifiedDigest = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    WebhookToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastWebhookReceivedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastTelegramNotifiedDigest = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DockerWatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DockerWatches_DockerConnections_DockerConnectionId",
                        column: x => x.DockerConnectionId,
                        principalTable: "DockerConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DockerWatches_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DockerWatches_WebResources_WebResourceId",
                        column: x => x.WebResourceId,
                        principalTable: "WebResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "WebResourceTags",
                columns: table => new
                {
                    WebResourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TagId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebResourceTags", x => new { x.WebResourceId, x.TagId });
                    table.ForeignKey(
                        name: "FK_WebResourceTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WebResourceTags_WebResources_WebResourceId",
                        column: x => x.WebResourceId,
                        principalTable: "WebResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DockerUpdateAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WebResourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DockerWatchId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DockerConnectionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ContainerId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    InitiatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActionType = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageReference = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ContainerName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PreviousDigest = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    NewDigest = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    HealthVerified = table.Column<bool>(type: "INTEGER", nullable: false),
                    HealthVerifiedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DockerUpdateAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DockerUpdateAttempts_DockerConnections_DockerConnectionId",
                        column: x => x.DockerConnectionId,
                        principalTable: "DockerConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_DockerUpdateAttempts_DockerWatches_DockerWatchId",
                        column: x => x.DockerWatchId,
                        principalTable: "DockerWatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DockerUpdateAttempts_WebResources_WebResourceId",
                        column: x => x.WebResourceId,
                        principalTable: "WebResources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_UserId_Name",
                table: "Categories",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Credentials_WebResourceId",
                table: "Credentials",
                column: "WebResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_DockerConnections_UserId_Name",
                table: "DockerConnections",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DockerUpdateAttempts_DockerConnectionId_CompletedUtc",
                table: "DockerUpdateAttempts",
                columns: new[] { "DockerConnectionId", "CompletedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DockerUpdateAttempts_DockerWatchId_CompletedUtc",
                table: "DockerUpdateAttempts",
                columns: new[] { "DockerWatchId", "CompletedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DockerUpdateAttempts_WebResourceId",
                table: "DockerUpdateAttempts",
                column: "WebResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_DockerWatches_DockerConnectionId",
                table: "DockerWatches",
                column: "DockerConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_DockerWatches_DockerConnectionId_ContainerName",
                table: "DockerWatches",
                columns: new[] { "DockerConnectionId", "ContainerName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DockerWatches_UserId_Enabled_LastCheckedUtc",
                table: "DockerWatches",
                columns: new[] { "UserId", "Enabled", "LastCheckedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DockerWatches_WebhookToken",
                table: "DockerWatches",
                column: "WebhookToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DockerWatches_WebResourceId",
                table: "DockerWatches",
                column: "WebResourceId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_FamilyId",
                table: "RefreshTokens",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenHash",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_UserId",
                table: "RefreshTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_UserId_Name",
                table: "Tags",
                columns: new[] { "UserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedEmail",
                table: "Users",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WebResources_CategoryId",
                table: "WebResources",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_WebResources_DockerConnectionId",
                table: "WebResources",
                column: "DockerConnectionId");

            migrationBuilder.CreateIndex(
                name: "IX_WebResources_UserId_Name",
                table: "WebResources",
                columns: new[] { "UserId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_WebResourceTags_TagId",
                table: "WebResourceTags",
                column: "TagId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Credentials");

            migrationBuilder.DropTable(
                name: "DockerUpdateAttempts");

            migrationBuilder.DropTable(
                name: "RefreshTokens");

            migrationBuilder.DropTable(
                name: "WebResourceTags");

            migrationBuilder.DropTable(
                name: "DockerWatches");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "WebResources");

            migrationBuilder.DropTable(
                name: "Categories");

            migrationBuilder.DropTable(
                name: "DockerConnections");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
