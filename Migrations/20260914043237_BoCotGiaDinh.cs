using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class BoCotGiaDinh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfirmedAssumptions",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PendingAssumptionGaps",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "PendingAssumptionsVersion",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "SpecAssumptionCorrections",
                table: "Projects");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConfirmedAssumptions",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingAssumptionGaps",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingAssumptionsVersion",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpecAssumptionCorrections",
                table: "Projects",
                type: "nvarchar(max)",
                nullable: true);
        }
    }
}
