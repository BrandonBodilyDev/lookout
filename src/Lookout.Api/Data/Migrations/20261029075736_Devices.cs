using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lookout.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Devices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    DeviceId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CrewId = table.Column<string>(type: "text", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AssignedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devices", x => x.DeviceId);
                    table.ForeignKey(
                        name: "FK_devices_crews_IncidentId_CrewId",
                        columns: x => new { x.IncidentId, x.CrewId },
                        principalTable: "crews",
                        principalColumns: new[] { "IncidentId", "CrewId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_devices_IncidentId_CrewId",
                table: "devices",
                columns: new[] { "IncidentId", "CrewId" });
        
            // The runtime role assigns, moves and removes devices.
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE, DELETE ON devices TO lookout_app;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "devices");
        }
    }
}
