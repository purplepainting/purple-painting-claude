using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleDashboard.Data.Migrations
{
    /// <inheritdoc />
    public partial class MonthlyBonuses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MigratedAt",
                table: "BonusPayouts",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BonusAdjustments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Person = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Month = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BonusAdjustments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BonusClawbacks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    JobName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Month = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Pool = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BonusClawbacks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BonusExclusions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Month = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    Person = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BonusExclusions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BonusPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Person = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Covers = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    JobId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    JobName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BonusPayments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BonusRetentionReleases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Month = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    Person = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BonusRetentionReleases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DashboardSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "EffortRatings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Month = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    MembershipId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Person = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Score = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    RatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EffortRatings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayLocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Person = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Month = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    LinesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Net = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    Owed = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    CheckNum = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CheckDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Gross = table.Column<decimal>(type: "decimal(12,2)", nullable: true),
                    LockedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LockedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayLocks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BonusAdjustments_Month",
                table: "BonusAdjustments",
                column: "Month");

            migrationBuilder.CreateIndex(
                name: "IX_BonusClawbacks_JobId",
                table: "BonusClawbacks",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_BonusExclusions_Month_Person",
                table: "BonusExclusions",
                columns: new[] { "Month", "Person" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BonusPayments_PaidOn",
                table: "BonusPayments",
                column: "PaidOn");

            migrationBuilder.CreateIndex(
                name: "IX_BonusRetentionReleases_Month",
                table: "BonusRetentionReleases",
                column: "Month");

            migrationBuilder.CreateIndex(
                name: "IX_EffortRatings_Month_MembershipId_RatedBy",
                table: "EffortRatings",
                columns: new[] { "Month", "MembershipId", "RatedBy" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayLocks_Kind_Person_Month",
                table: "PayLocks",
                columns: new[] { "Kind", "Person", "Month" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BonusAdjustments");

            migrationBuilder.DropTable(
                name: "BonusClawbacks");

            migrationBuilder.DropTable(
                name: "BonusExclusions");

            migrationBuilder.DropTable(
                name: "BonusPayments");

            migrationBuilder.DropTable(
                name: "BonusRetentionReleases");

            migrationBuilder.DropTable(
                name: "DashboardSettings");

            migrationBuilder.DropTable(
                name: "EffortRatings");

            migrationBuilder.DropTable(
                name: "PayLocks");

            migrationBuilder.DropColumn(
                name: "MigratedAt",
                table: "BonusPayouts");
        }
    }
}
