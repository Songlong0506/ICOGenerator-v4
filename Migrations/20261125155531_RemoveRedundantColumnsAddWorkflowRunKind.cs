using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class RemoveRedundantColumnsAddWorkflowRunKind : Migration
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

            migrationBuilder.DropColumn(
                name: "IsApproved",
                table: "ProjectDocuments");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "OrgUnits");

            migrationBuilder.DropColumn(
                name: "IsRead",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "Birthday",
                table: "Associates");

            migrationBuilder.DropColumn(
                name: "IsIndirect",
                table: "Associates");

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
                name: "IsApproved",
                table: "ProjectDocuments",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "OrgUnits",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsRead",
                table: "Notifications",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "Birthday",
                table: "Associates",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsIndirect",
                table: "Associates",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_RecipientUsername_IsRead_CreatedAt",
                table: "Notifications",
                columns: new[] { "RecipientUsername", "IsRead", "CreatedAt" });

            migrationBuilder.Sql(
                "UPDATE ProjectDocuments SET IsApproved = CASE WHEN VersionName = 'draft' THEN 0 ELSE 1 END");
            migrationBuilder.Sql(
                "UPDATE Notifications SET IsRead = CASE WHEN ReadAt IS NULL THEN 0 ELSE 1 END");
        }
    }
}
