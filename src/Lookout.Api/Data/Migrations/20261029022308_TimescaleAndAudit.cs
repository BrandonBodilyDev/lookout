using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lookout.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TimescaleAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TimescaleDB partitions the two time-series tables by time.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS timescaledb;");
            migrationBuilder.Sql("DO $$ BEGIN PERFORM create_hypertable('audit_events', by_range('Time', INTERVAL '1 day')); END $$;");
            migrationBuilder.Sql("DO $$ BEGIN PERFORM create_hypertable('crew_fixes', by_range('Time', INTERVAL '1 day')); END $$;");

            // The audit trail is append-only. Corrections are new events, never edits.
            migrationBuilder.Sql("""
                CREATE FUNCTION audit_events_reject_change() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'audit_events is append-only: % is not allowed', TG_OP
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER audit_events_append_only
                BEFORE UPDATE OR DELETE ON audit_events
                FOR EACH ROW EXECUTE FUNCTION audit_events_reject_change();
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER audit_events_no_truncate
                BEFORE TRUNCATE ON audit_events
                FOR EACH STATEMENT EXECUTE FUNCTION audit_events_reject_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_events_no_truncate ON audit_events;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS audit_events_append_only ON audit_events;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS audit_events_reject_change();");
            // Hypertables stay hypertables; dropping the tables in the Initial migration's Down removes them.
        }
    }
}
