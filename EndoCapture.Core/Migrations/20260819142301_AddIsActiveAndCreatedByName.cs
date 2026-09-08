using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Endoscopy.Migrations
{
    /// <inheritdoc />
    public partial class AddIsActiveAndCreatedByName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CreatedByName",
                table: "MediaCaptures",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "MediaCaptures",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaCaptures_IsActive",
                table: "MediaCaptures",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaCaptures_IsActive",
                table: "MediaCaptures");

            migrationBuilder.DropColumn(
                name: "CreatedByName",
                table: "MediaCaptures");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "MediaCaptures");
        }
    }
}
