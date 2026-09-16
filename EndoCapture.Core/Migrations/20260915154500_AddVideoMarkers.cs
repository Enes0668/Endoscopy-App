using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Endoscopy.Migrations
{
    /// <inheritdoc />
    public partial class AddVideoMarkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VideoMarkers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MediaCaptureId = table.Column<long>(type: "bigint", nullable: false),
                    TimestampMs = table.Column<long>(type: "bigint", nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VideoMarkers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VideoMarkers_MediaCaptures_MediaCaptureId",
                        column: x => x.MediaCaptureId,
                        principalTable: "MediaCaptures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VideoMarkers_MediaCaptureId",
                table: "VideoMarkers",
                column: "MediaCaptureId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VideoMarkers");
        }
    }
}
