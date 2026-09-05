using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class HarvestAtApprovalGates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hai hàng đợi cho các vòng học ở CỔNG DUYỆT: Product Brief (con trỏ theo phiên bản vừa duyệt)
            // và bản demo (cờ bật khi duyệt bước POC). Xem ChecklistGapMemoryService / PocFeedbackMemoryService.
            migrationBuilder.AddColumn<string>(
                name: "PendingChecklistHarvestVersion",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PendingPocFeedbackHarvest",
                table: "Projects",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingChecklistHarvestVersion",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PendingPocFeedbackHarvest",
                table: "Projects");
        }
    }
}
