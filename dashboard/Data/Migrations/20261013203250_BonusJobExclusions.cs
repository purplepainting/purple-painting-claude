using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PurpleDashboard.Data.Migrations
{
    /// <inheritdoc />
    public partial class BonusJobExclusions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BonusJobExclusions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    JobName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BonusJobExclusions", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "BonusJobExclusions",
                columns: new[] { "Id", "CreatedAt", "CreatedBy", "JobId", "JobName", "Reason" },
                values: new object[,]
                {
                    { 1, new DateTime(2026, 10, 13, 0, 0, 0, 0, DateTimeKind.Utc), "kareem@purplepainting.net", "22PX9MaHxFt8", "Goleta Valley Cottage Hospital", "Paid out in the spreadsheet bonus round; QuickBooks catch-up costs and old billing were added in August" },
                    { 2, new DateTime(2026, 10, 13, 0, 0, 0, 0, DateTimeKind.Utc), "kareem@purplepainting.net", "22PXjg7k8Eu3", "1509 Shoreline Dr", "Paid out in the spreadsheet bonus round; QuickBooks catch-up costs were added in August" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_BonusJobExclusions_JobId",
                table: "BonusJobExclusions",
                column: "JobId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BonusJobExclusions");
        }
    }
}
