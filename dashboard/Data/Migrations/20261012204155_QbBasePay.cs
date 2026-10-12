using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleDashboard.Data.Migrations
{
    /// <inheritdoc />
    public partial class QbBasePay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "QbBasePay",
                table: "EmployeeProfiles",
                type: "decimal(10,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QbPayType",
                table: "EmployeeProfiles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QbBasePay",
                table: "EmployeeProfiles");

            migrationBuilder.DropColumn(
                name: "QbPayType",
                table: "EmployeeProfiles");
        }
    }
}
