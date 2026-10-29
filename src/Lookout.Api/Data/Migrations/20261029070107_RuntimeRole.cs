using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lookout.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RuntimeRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The API serves requests as a member of lookout_app, not as the table owner. The owner runs
            // migrations; the runtime role can read and write what the app needs and nothing more. In
            // particular it can only INSERT and SELECT the audit trail: it cannot delete rows, drop
            // chunks, disable the append-only trigger, or alter the table. Login users are created by
            // deployment (deploy/postgres/ for docker compose) as members of this role.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'lookout_app') THEN
                        CREATE ROLE lookout_app NOLOGIN;
                    END IF;
                END $$;
                GRANT USAGE ON SCHEMA public TO lookout_app;
                GRANT SELECT, INSERT, UPDATE ON incidents, crews, safety_zones, escape_routes, alerts TO lookout_app;
                GRANT SELECT, INSERT ON audit_events, crew_fixes TO lookout_app;
                GRANT SELECT ON "__EFMigrationsHistory" TO lookout_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO lookout_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                REVOKE ALL ON incidents, crews, safety_zones, escape_routes, alerts, audit_events, crew_fixes, "__EFMigrationsHistory" FROM lookout_app;
                REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM lookout_app;
                REVOKE USAGE ON SCHEMA public FROM lookout_app;
                """);
        }
    }
}
