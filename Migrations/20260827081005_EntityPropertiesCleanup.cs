using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class EntityPropertiesCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Associates_GlobalId",
                table: "Associates");

            migrationBuilder.DropColumn(
                name: "IsVisionSource",
                table: "ProjectSourceFiles");

            migrationBuilder.DropColumn(
                name: "CostCenter",
                table: "OrgUnits");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "OrgUnits");

            migrationBuilder.DropColumn(
                name: "CreatedDate",
                table: "OrgUnits");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "OrgUnits");

            migrationBuilder.DropColumn(
                name: "DiscManagerLId",
                table: "OrgUnits");

            migrationBuilder.DropColumn(
                name: "DisciplinaryResponsible",
                table: "OrgUnits");

            migrationBuilder.DropColumn(
                name: "TypeOrganize",
                table: "OrgUnits");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                table: "OrgUnits");

            migrationBuilder.DropColumn(
                name: "UpdatedDate",
                table: "OrgUnits");

            migrationBuilder.DropColumn(
                name: "ReadAt",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "PromptVersionId",
                table: "EvalResults");

            migrationBuilder.DropColumn(
                name: "ActorRole",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "Birthday",
                table: "Associates");

            migrationBuilder.DropColumn(
                name: "Costcenter",
                table: "Associates");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                table: "Associates");

            migrationBuilder.DropColumn(
                name: "CreatedDate",
                table: "Associates");

            migrationBuilder.DropColumn(
                name: "EmployeeSubGroup",
                table: "Associates");

            migrationBuilder.DropColumn(
                name: "Gender",
                table: "Associates");

            migrationBuilder.DropColumn(
                name: "GlobalId",
                table: "Associates");

            migrationBuilder.DropColumn(
                name: "HiredDate",
                table: "Associates");

            migrationBuilder.DropColumn(
                name: "IsIndirect",
                table: "Associates");

            migrationBuilder.DropColumn(
                name: "LeadingPerson",
                table: "Associates");

            migrationBuilder.DropColumn(
                name: "Mobiphone",
                table: "Associates");

            migrationBuilder.DropColumn(
                name: "PickupAddress",
                table: "Associates");

            migrationBuilder.DropColumn(
                name: "StandardWorkingHour",
                table: "Associates");

            migrationBuilder.DropColumn(
                name: "UpdatedBy",
                table: "Associates");

            migrationBuilder.DropColumn(
                name: "UpdatedDate",
                table: "Associates");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsVisionSource",
                table: "ProjectSourceFiles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CostCenter",
                table: "OrgUnits",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "OrgUnits",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedDate",
                table: "OrgUnits",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "OrgUnits",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscManagerLId",
                table: "OrgUnits",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisciplinaryResponsible",
                table: "OrgUnits",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TypeOrganize",
                table: "OrgUnits",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedBy",
                table: "OrgUnits",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedDate",
                table: "OrgUnits",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReadAt",
                table: "Notifications",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PromptVersionId",
                table: "EvalResults",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActorRole",
                table: "AuditLogs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "Birthday",
                table: "Associates",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Costcenter",
                table: "Associates",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedBy",
                table: "Associates",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedDate",
                table: "Associates",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "EmployeeSubGroup",
                table: "Associates",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Gender",
                table: "Associates",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GlobalId",
                table: "Associates",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HiredDate",
                table: "Associates",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsIndirect",
                table: "Associates",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LeadingPerson",
                table: "Associates",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mobiphone",
                table: "Associates",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PickupAddress",
                table: "Associates",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "StandardWorkingHour",
                table: "Associates",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "UpdatedBy",
                table: "Associates",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedDate",
                table: "Associates",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Associates_GlobalId",
                table: "Associates",
                column: "GlobalId");
        }
    }
}
