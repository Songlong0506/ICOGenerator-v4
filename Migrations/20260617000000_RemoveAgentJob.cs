using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAgentJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Luồng chat BA bất đồng bộ (AgentJobRunner + endpoint Start/JobStatus) đã được gỡ;
            // bảng AgentJobs trở thành mồ côi (không entity/DbSet nào dùng). Drop bảng.
            migrationBuilder.DropTable(
                name: "AgentJobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Tạo lại y như định nghĩa gốc (migration v1) để rollback được.
            migrationBuilder.CreateTable(
                name: "AgentJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CurrentStep = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UserMessage = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Result = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FinishedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentJobs", x => x.Id);
                });
        }
    }
}
