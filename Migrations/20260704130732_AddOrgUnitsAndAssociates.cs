using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class AddOrgUnitsAndAssociates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Associates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    PersonalNumber = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    GlobalId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    OrgUnitCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    OrganizationUnit = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Mobiphone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PickupAddress = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Gender = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Position = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    StandardWorkingHour = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Costcenter = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    LeadingPerson = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    EmployeeSubGroup = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    HiredDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsIndirect = table.Column<bool>(type: "bit", nullable: false),
                    LeavingDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Birthday = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Associates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OrgUnits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsDelete = table.Column<bool>(type: "bit", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CostCenter = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DiscManagerLId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DisciplinaryResponsible = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OrgUnitCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TargetResponsible = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TrgtManagerLId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TypeOrganize = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    IsDepartment = table.Column<bool>(type: "bit", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrgUnits", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Associates_GlobalId",
                table: "Associates",
                column: "GlobalId");

            migrationBuilder.CreateIndex(
                name: "IX_Associates_OrgUnitCode",
                table: "Associates",
                column: "OrgUnitCode");

            migrationBuilder.CreateIndex(
                name: "IX_OrgUnits_OrgUnitCode",
                table: "OrgUnits",
                column: "OrgUnitCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Associates");

            migrationBuilder.DropTable(
                name: "OrgUnits");
        }
    }
}
