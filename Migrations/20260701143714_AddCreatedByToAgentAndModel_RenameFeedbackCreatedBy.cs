using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class AddCreatedByToAgentAndModel_RenameFeedbackCreatedBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SubmittedByUsername",
                table: "Feedbacks",
                newName: "CreatedByUsername");

            migrationBuilder.RenameIndex(
                name: "IX_Feedbacks_SubmittedByUsername_CreatedAt",
                table: "Feedbacks",
                newName: "IX_Feedbacks_CreatedByUsername_CreatedAt");

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUsername",
                table: "AiModels",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUsername",
                table: "Agents",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedByUsername",
                table: "AiModels");

            migrationBuilder.DropColumn(
                name: "CreatedByUsername",
                table: "Agents");

            migrationBuilder.RenameColumn(
                name: "CreatedByUsername",
                table: "Feedbacks",
                newName: "SubmittedByUsername");

            migrationBuilder.RenameIndex(
                name: "IX_Feedbacks_CreatedByUsername_CreatedAt",
                table: "Feedbacks",
                newName: "IX_Feedbacks_SubmittedByUsername_CreatedAt");
        }
    }
}
