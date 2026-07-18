using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class RequirementPocExperience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DecisionHarvestedTurnCount",
                table: "Projects",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DecisionLog",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PocFeedbackHarvestedCount",
                table: "Projects",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "SuggestionsMultiSelect",
                table: "AgentConversations",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DecisionHarvestedTurnCount",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DecisionLog",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PocFeedbackHarvestedCount",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "SuggestionsMultiSelect",
                table: "AgentConversations");
        }
    }
}
