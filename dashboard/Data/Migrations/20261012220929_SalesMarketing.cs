using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleDashboard.Data.Migrations
{
    /// <inheritdoc />
    public partial class SalesMarketing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdSpends",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Month = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    Leads = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdSpends", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CommissionPayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    JobId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    JobName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Person = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    PaidOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommissionPayments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CommissionRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Person = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Percent = table.Column<decimal>(type: "decimal(6,3)", nullable: false),
                    Basis = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommissionRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalesActivities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Rep = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Doors = table.Column<int>(type: "int", nullable: false),
                    Leads = table.Column<int>(type: "int", nullable: false),
                    Sold = table.Column<int>(type: "int", nullable: false),
                    SoldAmount = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesActivities", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdSpends_Month_Channel",
                table: "AdSpends",
                columns: new[] { "Month", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommissionPayments_JobId",
                table: "CommissionPayments",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_CommissionRules_Person",
                table: "CommissionRules",
                column: "Person",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesActivities_Date",
                table: "SalesActivities",
                column: "Date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdSpends");

            migrationBuilder.DropTable(
                name: "CommissionPayments");

            migrationBuilder.DropTable(
                name: "CommissionRules");

            migrationBuilder.DropTable(
                name: "SalesActivities");
        }
    }
}
