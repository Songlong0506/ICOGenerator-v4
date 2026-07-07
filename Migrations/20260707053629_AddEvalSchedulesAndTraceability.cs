using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class AddEvalSchedulesAndTraceability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BaselineEvalRunId",
                table: "EvalRuns",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsRegression",
                table: "EvalRuns",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ScheduleId",
                table: "EvalRuns",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ScoreDelta",
                table: "EvalRuns",
                type: "float",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "EvalSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PromptKey = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    TargetModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetModelName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    JudgeModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JudgeModelName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IntervalHours = table.Column<int>(type: "int", nullable: false),
                    RegressionThreshold = table.Column<double>(type: "float", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    NextRunAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastEnqueuedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EvalSchedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProjectTraceabilities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MatrixJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModelName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TotalTokens = table.Column<int>(type: "int", nullable: false),
                    GeneratedByUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    GeneratedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectTraceabilities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectTraceabilities_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EvalRuns_ScheduleId",
                table: "EvalRuns",
                column: "ScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_EvalSchedules_IsEnabled_NextRunAt",
                table: "EvalSchedules",
                columns: new[] { "IsEnabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectTraceabilities_ProjectId",
                table: "ProjectTraceabilities",
                column: "ProjectId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EvalSchedules");

            migrationBuilder.DropTable(
                name: "ProjectTraceabilities");

            migrationBuilder.DropIndex(
                name: "IX_EvalRuns_ScheduleId",
                table: "EvalRuns");

            migrationBuilder.DropColumn(
                name: "BaselineEvalRunId",
                table: "EvalRuns");

            migrationBuilder.DropColumn(
                name: "IsRegression",
                table: "EvalRuns");

            migrationBuilder.DropColumn(
                name: "ScheduleId",
                table: "EvalRuns");

            migrationBuilder.DropColumn(
                name: "ScoreDelta",
                table: "EvalRuns");
        }
    }
}
