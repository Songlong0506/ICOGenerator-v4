using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUnusedColumnsAddWorkflowRunKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ReadAt trở thành cờ "đã đọc" duy nhất: dòng đã đọc mà thiếu ReadAt (không nên có, nhưng phòng hờ)
            // lấy tạm CreatedAt để không bật lại thành "chưa đọc" khi bỏ cột IsRead.
            migrationBuilder.Sql(
                "UPDATE Notifications SET ReadAt = CreatedAt WHERE IsRead = 1 AND ReadAt IS NULL");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_RecipientUsername_IsRead_CreatedAt",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_Associates_GlobalId",
                table: "Associates");

            migrationBuilder.DropColumn(
                name: "IsVisionSource",
                table: "ProjectSourceFiles");

            migrationBuilder.DropColumn(
                name: "PageCount",
                table: "ProjectSourceFiles");

            migrationBuilder.DropColumn(
                name: "IsApproved",
                table: "ProjectDocuments");

            migrationBuilder.DropColumn(
                name: "TokenUsed",
                table: "ProjectDocuments");

            migrationBuilder.DropColumn(
                name: "CostCenter",
                table: "OrgUnits");

            migrationBuilder.DropColumn(
                name: "IsRead",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "WorkflowRunId",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "JudgeAuditCases");

            migrationBuilder.DropColumn(
                name: "PromptVersionId",
                table: "EvalResults");

            migrationBuilder.DropColumn(
                name: "Costcenter",
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
                name: "OrgUnitName",
                table: "AppUsers");

            migrationBuilder.DropColumn(
                name: "CreatedByUsername",
                table: "Agents");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "WorkflowRuns",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Delivery");

            // Suy Kind cho run cũ từ đúng chuỗi tên mà code trước đây dùng để nhận diện; còn lại là Delivery.
            migrationBuilder.Sql(
                "UPDATE WorkflowRuns SET Kind = 'RequirementDraft' WHERE Name = 'Write Requirement'");
            migrationBuilder.Sql(
                "UPDATE WorkflowRuns SET Kind = 'RequirementDesignSpec' WHERE Name LIKE 'Requirement Design Spec%'");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecipientUsername_ReadAt_CreatedAt",
                table: "Notifications",
                columns: new[] { "RecipientUsername", "ReadAt", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_RecipientUsername_ReadAt_CreatedAt",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "WorkflowRuns");

            migrationBuilder.AddColumn<bool>(
                name: "IsVisionSource",
                table: "ProjectSourceFiles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PageCount",
                table: "ProjectSourceFiles",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsApproved",
                table: "ProjectDocuments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TokenUsed",
                table: "ProjectDocuments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "CostCenter",
                table: "OrgUnits",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsRead",
                table: "Notifications",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "WorkflowRunId",
                table: "Notifications",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "JudgeAuditCases",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "PromptVersionId",
                table: "EvalResults",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Costcenter",
                table: "Associates",
                type: "nvarchar(50)",
                maxLength: 50,
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

            migrationBuilder.AddColumn<string>(
                name: "OrgUnitName",
                table: "AppUsers",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUsername",
                table: "Agents",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecipientUsername_IsRead_CreatedAt",
                table: "Notifications",
                columns: new[] { "RecipientUsername", "IsRead", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Associates_GlobalId",
                table: "Associates",
                column: "GlobalId");

            migrationBuilder.Sql(
                "UPDATE ProjectDocuments SET IsApproved = CASE WHEN VersionName = 'draft' THEN 0 ELSE 1 END");
            migrationBuilder.Sql(
                "UPDATE Notifications SET IsRead = CASE WHEN ReadAt IS NULL THEN 0 ELSE 1 END");
        }
    }
}
