using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class HardeningReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Deleting an Agent should no longer cascade-wipe its audit history (call logs /
            // conversations). Re-point those Agent FKs to Restrict.
            migrationBuilder.DropForeignKey(
                name: "FK_AgentConversations_Agents_AgentId",
                table: "AgentConversations");

            migrationBuilder.DropForeignKey(
                name: "FK_AgentModelCallLogs_Agents_AgentId",
                table: "AgentModelCallLogs");

            // Shrink the enum-as-string columns that were nvarchar(max) so they are normal,
            // indexable columns instead of LOBs.
            migrationBuilder.AlterColumn<string>(
                name: "Role",
                table: "AgentConversations",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "CurrentStage",
                table: "WorkflowRuns",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "AgentTasks",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.AddForeignKey(
                name: "FK_AgentConversations_Agents_AgentId",
                table: "AgentConversations",
                column: "AgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentModelCallLogs_Agents_AgentId",
                table: "AgentModelCallLogs",
                column: "AgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentConversations_Agents_AgentId",
                table: "AgentConversations");

            migrationBuilder.DropForeignKey(
                name: "FK_AgentModelCallLogs_Agents_AgentId",
                table: "AgentModelCallLogs");

            migrationBuilder.AlterColumn<string>(
                name: "Role",
                table: "AgentConversations",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<string>(
                name: "CurrentStage",
                table: "WorkflowRuns",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "AgentTasks",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentConversations_Agents_AgentId",
                table: "AgentConversations",
                column: "AgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentModelCallLogs_Agents_AgentId",
                table: "AgentModelCallLogs",
                column: "AgentId",
                principalTable: "Agents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
