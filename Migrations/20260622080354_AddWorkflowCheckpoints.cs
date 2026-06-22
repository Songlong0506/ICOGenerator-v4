using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PendingApprovalJson",
                table: "WorkflowRuns",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "ToolDefinitions",
                type: "nvarchar(3000)",
                maxLength: 3000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(1000)",
                oldMaxLength: 1000);

            migrationBuilder.CreateTable(
                name: "WorkflowCheckpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CheckpointId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ParentCheckpointId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Data = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowCheckpoints", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowCheckpoints_SessionId",
                table: "WorkflowCheckpoints",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowCheckpoints_SessionId_CheckpointId",
                table: "WorkflowCheckpoints",
                columns: new[] { "SessionId", "CheckpointId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkflowCheckpoints");

            migrationBuilder.DropColumn(
                name: "PendingApprovalJson",
                table: "WorkflowRuns");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "ToolDefinitions",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(3000)",
                oldMaxLength: 3000);
        }
    }
}
