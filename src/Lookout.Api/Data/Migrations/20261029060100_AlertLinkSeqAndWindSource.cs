using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lookout.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlertLinkSeqAndWindSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WindSource",
                table: "incidents",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "LinkSeq",
                table: "alerts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_alerts_IncidentId_CrewId_LinkSeq",
                table: "alerts",
                columns: new[] { "IncidentId", "CrewId", "LinkSeq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_alerts_IncidentId_CrewId_LinkSeq",
                table: "alerts");

            migrationBuilder.DropColumn(
                name: "WindSource",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "LinkSeq",
                table: "alerts");
        }
    }
}
