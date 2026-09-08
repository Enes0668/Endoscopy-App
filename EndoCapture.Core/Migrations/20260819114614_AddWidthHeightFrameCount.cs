using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Endoscopy.Migrations
{
    /// <inheritdoc />
    public partial class AddWidthHeightFrameCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "FrameCount",
                table: "MediaCaptures",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Height",
                table: "MediaCaptures",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Width",
                table: "MediaCaptures",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FrameCount",
                table: "MediaCaptures");

            migrationBuilder.DropColumn(
                name: "Height",
                table: "MediaCaptures");

            migrationBuilder.DropColumn(
                name: "Width",
                table: "MediaCaptures");
        }
    }
}
