## Summary

<!-- What does this change and why? Link the issue or backlog item (e.g. "Closes #123", "P1-5"). -->

## Type of change

- [ ] Bug fix
- [ ] Feature
- [ ] Metadata only (DocTypes, workflows, print formats)
- [ ] Database migration
- [ ] Deployment / configuration
- [ ] Documentation
- [ ] Refactoring / maintenance

## How it was tested

<!-- Commands run and what you checked by hand. Screenshots for UI changes. -->

- [ ] `dotnet build` passes
- [ ] `dotnet run --project tests/Framework.Tests` passes (in-memory)
- [ ] With `FROGFRAME_TEST_PG` set (PostgreSQL), if persistence, reporting or migrations changed
- [ ] New or changed behaviour has a test

## Checklist

- [ ] UI text, code comments and documentation are in English (see `GEMINI.md`)
- [ ] Documentation updated (`docs/`, `README.md`, `TODO.md`) where behaviour or configuration changed
- [ ] Permissions respected: new data access goes through `DocumentService` / `IPermissionService` (row scope, field permissions)
- [ ] No secrets, real customer data or credentials in code, tests, metadata or screenshots
- [ ] New configuration has a safe default and is documented (`appsettings.json`, `.env.example`)

### If this adds a database migration

- [ ] New file `src/Framework.Persistence.PostgreSql/Migrations/NNN_name.sql` with the next free number
- [ ] Safe during a rolling update: adds tables/columns/indexes only; no drop or rename of something the current release still uses (see `docs/guides/database-migrations.md`)
- [ ] Tested with `--migrate-only` on a copy of a database with data

## Deployment notes

<!-- Anything an operator must do: new .env values, manual steps, reindexing, expected migration time. "None" if nothing. -->
