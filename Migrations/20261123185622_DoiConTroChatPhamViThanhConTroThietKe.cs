using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class DoiConTroChatPhamViThanhConTroThietKe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "InterviewScopeHarvestedTurnCount",
                table: "Projects",
                newName: "UxDesignedTurnCount");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "UxDesignedTurnCount",
                table: "Projects",
                newName: "InterviewScopeHarvestedTurnCount");
        }
    }
}
