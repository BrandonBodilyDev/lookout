-- Runs once, on a fresh database volume (docker-entrypoint-initdb.d).
-- The API's runtime login. Its privileges come from membership in lookout_app, which the
-- RuntimeRole migration creates and grants. Development password only.
DO $$
BEGIN
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'lookout_app') THEN
        CREATE ROLE lookout_app NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'lookout_runtime') THEN
        CREATE ROLE lookout_runtime LOGIN PASSWORD 'lookout-runtime-dev' IN ROLE lookout_app;
    END IF;
END $$;
