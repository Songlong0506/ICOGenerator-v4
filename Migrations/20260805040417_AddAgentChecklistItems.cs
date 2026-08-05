using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentChecklistItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentChecklistItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DomainKey = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Text = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Rationale = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                    Evidence = table.Column<string>(type: "nvarchar(600)", maxLength: 600, nullable: true),
                    SourceKind = table.Column<int>(type: "int", nullable: false),
                    SourceProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentChecklistItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentChecklistItems_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentChecklistItems_Projects_SourceProjectId",
                        column: x => x.SourceProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentChecklistItems_AgentId_DomainKey_Status",
                table: "AgentChecklistItems",
                columns: new[] { "AgentId", "DomainKey", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentChecklistItems_SourceProjectId",
                table: "AgentChecklistItems",
                column: "SourceProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentChecklistItems");
        }
    }
}
