using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleDashboard.Data.Migrations
{
    /// <inheritdoc />
    public partial class GoogleConnection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GoogleConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RefreshTokenProtected = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    ConnectedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConnectedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AdsCustomerId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    AdsLoginCustomerId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    AdsCustomerName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    BusinessAccount = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BusinessLocation = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    BusinessLocationTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GoogleConnections", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GoogleConnections");
        }
    }
}
