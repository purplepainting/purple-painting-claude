using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PurpleDashboard.Data.Migrations
{
    /// <inheritdoc />
    public partial class Hiring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CandidateDocuments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CandidateId = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Data = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UploadedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CandidateDocuments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HiringCandidates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Position = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Experience = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Stage = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    StageAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Rating = table.Column<int>(type: "int", nullable: true),
                    SheetJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    InterviewedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Interviewer = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TrialDate = table.Column<DateOnly>(type: "date", nullable: true),
                    TrialSupervisor = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    TrialNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TrialHours = table.Column<decimal>(type: "decimal(6,2)", nullable: true),
                    HiredOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Onboarding = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    NotHiredReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HiringCandidates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HiringEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CandidateId = table.Column<int>(type: "int", nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FromStage = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    ToStage = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    By = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HiringEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InterviewBookings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DayId = table.Column<int>(type: "int", nullable: false),
                    CandidateId = table.Column<int>(type: "int", nullable: false),
                    Time = table.Column<TimeOnly>(type: "time", nullable: false),
                    ReminderSentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReminderSentBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ConfirmedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InterviewBookings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InterviewDays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Location = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Start = table.Column<TimeOnly>(type: "time", nullable: false),
                    End = table.Column<TimeOnly>(type: "time", nullable: false),
                    SlotMinutes = table.Column<int>(type: "int", nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InterviewDays", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CandidateDocuments_CandidateId",
                table: "CandidateDocuments",
                column: "CandidateId");

            migrationBuilder.CreateIndex(
                name: "IX_HiringCandidates_Stage",
                table: "HiringCandidates",
                column: "Stage");

            migrationBuilder.CreateIndex(
                name: "IX_HiringEvents_CandidateId",
                table: "HiringEvents",
                column: "CandidateId");

            migrationBuilder.CreateIndex(
                name: "IX_InterviewBookings_CandidateId",
                table: "InterviewBookings",
                column: "CandidateId");

            migrationBuilder.CreateIndex(
                name: "IX_InterviewBookings_DayId_CandidateId",
                table: "InterviewBookings",
                columns: new[] { "DayId", "CandidateId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InterviewBookings_DayId_Time",
                table: "InterviewBookings",
                columns: new[] { "DayId", "Time" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InterviewDays_Date",
                table: "InterviewDays",
                column: "Date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CandidateDocuments");

            migrationBuilder.DropTable(
                name: "HiringCandidates");

            migrationBuilder.DropTable(
                name: "HiringEvents");

            migrationBuilder.DropTable(
                name: "InterviewBookings");

            migrationBuilder.DropTable(
                name: "InterviewDays");
        }
    }
}
