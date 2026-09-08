using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Endoscopy.Migrations
{
    /// <inheritdoc />
    public partial class AddPatientDoctorProcedureFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DoctorName",
                table: "MediaCaptures",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PatientIdentifier",
                table: "MediaCaptures",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PatientName",
                table: "MediaCaptures",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcedureType",
                table: "MediaCaptures",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoomName",
                table: "MediaCaptures",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaCaptures_DoctorName",
                table: "MediaCaptures",
                column: "DoctorName");

            migrationBuilder.CreateIndex(
                name: "IX_MediaCaptures_PatientIdentifier",
                table: "MediaCaptures",
                column: "PatientIdentifier");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MediaCaptures_DoctorName",
                table: "MediaCaptures");

            migrationBuilder.DropIndex(
                name: "IX_MediaCaptures_PatientIdentifier",
                table: "MediaCaptures");

            migrationBuilder.DropColumn(
                name: "DoctorName",
                table: "MediaCaptures");

            migrationBuilder.DropColumn(
                name: "PatientIdentifier",
                table: "MediaCaptures");

            migrationBuilder.DropColumn(
                name: "PatientName",
                table: "MediaCaptures");

            migrationBuilder.DropColumn(
                name: "ProcedureType",
                table: "MediaCaptures");

            migrationBuilder.DropColumn(
                name: "RoomName",
                table: "MediaCaptures");
        }
    }
}
