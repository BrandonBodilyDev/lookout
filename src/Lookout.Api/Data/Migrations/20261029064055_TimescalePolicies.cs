using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lookout.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TimescalePolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Crew fixes: compress after a week, keep 90 days. Positions are operational data, not a record.
            migrationBuilder.Sql("""
                ALTER TABLE crew_fixes SET (timescaledb.compress, timescaledb.compress_segmentby = '"IncidentId", "CrewId"');
                SELECT add_compression_policy('crew_fixes', INTERVAL '7 days');
                SELECT add_retention_policy('crew_fixes', INTERVAL '90 days');
                """);
            // Audit events: compress after 30 days, never dropped. Compression keeps rows queryable, and
            // the append-only trigger still blocks row edits.
            migrationBuilder.Sql("""
                ALTER TABLE audit_events SET (timescaledb.compress, timescaledb.compress_segmentby = '"IncidentId"');
                SELECT add_compression_policy('audit_events', INTERVAL '30 days');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("SELECT remove_retention_policy('crew_fixes', if_exists => true);");
            migrationBuilder.Sql("SELECT remove_compression_policy('crew_fixes', if_exists => true);");
            migrationBuilder.Sql("SELECT remove_compression_policy('audit_events', if_exists => true);");
        }
    }
}
