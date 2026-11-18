-- Removes the KPI widgets and dashboards created by kpi_widgets_dashboards.sql.
-- Usage:  psql "$DATABASE_URL" -v tenant=acme -f kpi_widgets_dashboards_remove.sql
\set ON_ERROR_STOP on
\if :{?tenant}
\else
  \echo 'Pass the tenant id, e.g. -v tenant=acme'
  \quit
\endif
BEGIN;
DELETE FROM frogframe_dashboards WHERE tenant_id = :'tenant' AND id LIKE 'd\_kpi\_%';
DELETE FROM frogframe_dashboard_widgets WHERE tenant_id = :'tenant' AND id LIKE 'w\_kpi\_%';
COMMIT;
