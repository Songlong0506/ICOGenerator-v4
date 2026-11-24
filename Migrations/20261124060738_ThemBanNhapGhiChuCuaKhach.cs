using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ICOGenerator.Migrations
{
    /// <inheritdoc />
    public partial class ThemBanNhapGhiChuCuaKhach : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PocShareLinkId",
                table: "PocComments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PocComments_ProjectId_PocShareLinkId",
                table: "PocComments",
                columns: new[] { "ProjectId", "PocShareLinkId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PocComments_ProjectId_PocShareLinkId",
                table: "PocComments");

            migrationBuilder.DropColumn(
                name: "PocShareLinkId",
                table: "PocComments");
        }
    }
}
