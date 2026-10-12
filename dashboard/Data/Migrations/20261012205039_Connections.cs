using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleDashboard.Data.Migrations
{
    /// <inheritdoc />
    public partial class Connections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JobTreadUserKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserObjectId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    UserEmail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    GrantKeyProtected = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    JobTreadUserId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    JobTreadUserName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SavedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobTreadUserKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QuickBooksConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RealmId = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CompanyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RefreshTokenProtected = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RefreshTokenExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConnectedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConnectedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Scopes = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuickBooksConnections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JobTreadUserKeys_UserObjectId",
                table: "JobTreadUserKeys",
                column: "UserObjectId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobTreadUserKeys");

            migrationBuilder.DropTable(
                name: "QuickBooksConnections");
        }
    }
}
