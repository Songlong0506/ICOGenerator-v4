using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class ThemModelTrongTaiVaKiemToan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "JudgeAiModelId",
                table: "Agents",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "JudgeAuditRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SourceModelId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    From = table.Column<DateTime>(type: "datetime2", nullable: true),
                    To = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SampleSize = table.Column<int>(type: "int", nullable: false),
                    CandidateModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CandidateModelName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ArbiterModelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArbiterModelName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ArbitrateAll = table.Column<bool>(type: "bit", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CaseCount = table.Column<int>(type: "int", nullable: false),
                    CompletedCount = table.Column<int>(type: "int", nullable: false),
                    DisagreementCount = table.Column<int>(type: "int", nullable: false),
                    TotalCost = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CancelRequestedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUsername = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JudgeAuditRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JudgeAuditCases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JudgeAuditRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceLogId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceModelId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SourceCreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BaselineClaimsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BaselineUnreadable = table.Column<bool>(type: "bit", nullable: false),
                    CandidateClaimsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CandidateUnreadable = table.Column<bool>(type: "bit", nullable: false),
                    GoldClaimsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ArbiterUsed = table.Column<bool>(type: "bit", nullable: false),
                    ArbiterNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Disagreement = table.Column<bool>(type: "bit", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Cost = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JudgeAuditCases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JudgeAuditCases_JudgeAuditRuns_JudgeAuditRunId",
                        column: x => x.JudgeAuditRunId,
                        principalTable: "JudgeAuditRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Agents_JudgeAiModelId",
                table: "Agents",
                column: "JudgeAiModelId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentModelCallLogs_Purpose_CreatedAt",
                table: "AgentModelCallLogs",
                columns: new[] { "Purpose", "CreatedAt" })
                .Annotation("SqlServer:Include", new[] { "ModelId", "IsSuccess" });

            migrationBuilder.CreateIndex(
                name: "IX_JudgeAuditCases_JudgeAuditRunId",
                table: "JudgeAuditCases",
                column: "JudgeAuditRunId");

            migrationBuilder.CreateIndex(
                name: "IX_JudgeAuditRuns_CreatedAt",
                table: "JudgeAuditRuns",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_JudgeAuditRuns_Status_CreatedAt",
                table: "JudgeAuditRuns",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Agents_AiModels_JudgeAiModelId",
                table: "Agents",
                column: "JudgeAiModelId",
                principalTable: "AiModels",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Agents_AiModels_JudgeAiModelId",
                table: "Agents");

            migrationBuilder.DropTable(
                name: "JudgeAuditCases");

            migrationBuilder.DropTable(
                name: "JudgeAuditRuns");

            migrationBuilder.DropIndex(
                name: "IX_Agents_JudgeAiModelId",
                table: "Agents");

            migrationBuilder.DropIndex(
                name: "IX_AgentModelCallLogs_Purpose_CreatedAt",
                table: "AgentModelCallLogs");

            migrationBuilder.DropColumn(
                name: "JudgeAiModelId",
                table: "Agents");
        }
    }
}
