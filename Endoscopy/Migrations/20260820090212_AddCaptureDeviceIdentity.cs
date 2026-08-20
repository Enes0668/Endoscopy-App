using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Endoscopy.Migrations
{
    /// <inheritdoc />
    public partial class AddCaptureDeviceIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LocalIpAddress",
                table: "MediaCaptures",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocalMacAddress",
                table: "MediaCaptures",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MachineName",
                table: "MediaCaptures",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LocalIpAddress",
                table: "MediaCaptures");

            migrationBuilder.DropColumn(
                name: "LocalMacAddress",
                table: "MediaCaptures");

            migrationBuilder.DropColumn(
                name: "MachineName",
                table: "MediaCaptures");
        }
    }
}
