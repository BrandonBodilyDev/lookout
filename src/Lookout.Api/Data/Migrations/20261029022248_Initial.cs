using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Lookout.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    Time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AlertId = table.Column<Guid>(type: "uuid", nullable: true),
                    CrewId = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Actor = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Details = table.Column<JsonDocument>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => new { x.Id, x.Time });
                });

            migrationBuilder.CreateTable(
                name: "crew_fixes",
                columns: table => new
                {
                    Time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CrewId = table.Column<string>(type: "text", nullable: false),
                    Position = table.Column<Point>(type: "geometry(Point, 4326)", nullable: false),
                    ScenarioMin = table.Column<double>(type: "double precision", nullable: false),
                    Bytes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crew_fixes", x => new { x.IncidentId, x.CrewId, x.Time });
                });

            migrationBuilder.CreateTable(
                name: "incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ScenarioId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClockRate = table.Column<double>(type: "double precision", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CrewId = table.Column<string>(type: "text", nullable: false),
                    Level = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequiresAck = table.Column<bool>(type: "boolean", nullable: false),
                    Text = table.Column<string>(type: "character varying(270)", maxLength: 270, nullable: false),
                    Bytes = table.Column<int>(type: "integer", nullable: false),
                    ScenarioMin = table.Column<double>(type: "double precision", nullable: false),
                    RaisedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SendCount = table.Column<int>(type: "integer", nullable: false),
                    EscalatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "text", nullable: true),
                    SupersededBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CancelReason = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alerts", x => x.Id);
                    table.CheckConstraint("ck_alerts_ack_has_actor", "\"State\" <> 'Acknowledged' OR (\"AcknowledgedAt\" IS NOT NULL AND \"AcknowledgedBy\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_alerts_incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "crews",
                columns: table => new
                {
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CrewId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    RouteId = table.Column<string>(type: "text", nullable: false),
                    Size = table.Column<int>(type: "integer", nullable: false),
                    Pace = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crews", x => new { x.IncidentId, x.CrewId });
                    table.ForeignKey(
                        name: "FK_crews_incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "escape_routes",
                columns: table => new
                {
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    RouteId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    SafetyZoneId = table.Column<string>(type: "text", nullable: false),
                    Path = table.Column<LineString>(type: "geometry(LineStringZ, 4326)", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_escape_routes", x => new { x.IncidentId, x.RouteId });
                    table.ForeignKey(
                        name: "FK_escape_routes_incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "safety_zones",
                columns: table => new
                {
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    ZoneId = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Area = table.Column<Polygon>(type: "geometry(Polygon, 4326)", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_safety_zones", x => new { x.IncidentId, x.ZoneId });
                    table.ForeignKey(
                        name: "FK_safety_zones_incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalTable: "incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alerts_IncidentId_CrewId_State",
                table: "alerts",
                columns: new[] { "IncidentId", "CrewId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_IncidentId_Time",
                table: "audit_events",
                columns: new[] { "IncidentId", "Time" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alerts");

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "crew_fixes");

            migrationBuilder.DropTable(
                name: "crews");

            migrationBuilder.DropTable(
                name: "escape_routes");

            migrationBuilder.DropTable(
                name: "safety_zones");

            migrationBuilder.DropTable(
                name: "incidents");
        }
    }
}
