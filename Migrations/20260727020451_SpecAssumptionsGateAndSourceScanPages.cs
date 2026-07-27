using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class SpecAssumptionsGateAndSourceScanPages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ScannedPageImageCount",
                table: "ProjectSourceFiles",
                type: "int",
                nullable: false,
                defaultValue: 0);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScannedPageImageCount",
                table: "ProjectSourceFiles");

            migrationBuilder.DropColumn(
                name: "PendingAssumptionsVersion",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "SpecAssumptionCorrections",
                table: "Projects");
        }
    }
}
