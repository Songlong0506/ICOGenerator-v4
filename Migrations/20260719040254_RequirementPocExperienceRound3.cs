using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class RequirementPocExperienceRound3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InterviewOutlookHarvestedTurnCount",
                table: "Projects",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "OpenQuestions",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlannedScope",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WorkedExamples",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InterviewOutlookHarvestedTurnCount",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "OpenQuestions",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PlannedScope",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "WorkedExamples",
                table: "Projects");
        }
    }
}
