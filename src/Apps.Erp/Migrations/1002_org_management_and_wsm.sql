-- ============================================================================
-- Migration 1002: Enterprise Organization Management (OM) & Workforce Scheduling (WSM)
-- 1. Seeds the 8 system OrganizationUnitType records for existing tenants.
-- 2. Backfills base_unit_code, status, materialized_path, depth, and breadcrumb on OrganizationUnit.
-- 3. Creates specialized PostgreSQL composite and text_pattern_ops indexes.
-- ============================================================================

-- 1. Seed standard OrganizationUnitType master documents for every tenant present in frogframe_documents
WITH tenants AS (
    SELECT DISTINCT tenant_id FROM frogframe_documents
    UNION
    SELECT 'acme'
),
default_types (type_name, type_code, hierarchy_order, parent_type, description) AS (
    VALUES
        ('Board of Directors', 'BOD',     1, NULL,                 'Executive board level'),
        ('Directorate',        'DIR',     2, 'Board of Directors', 'Directorate level'),
        ('Division',           'DIV',     3, 'Directorate',        'Division level'),
        ('Department',         'DEPT',    4, NULL,                 'Department level'),
        ('Sub-department',     'SUBDEPT', 5, 'Department',         'Sub-department level'),
        ('Section',            'SEC',     6, 'Department',         'Section level'),
        ('Team',               'TEAM',    7, NULL,                 'Operational team or squad'),
        ('Branch',             'BRANCH',  3, NULL,                 'Regional branch or office')
)
INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
SELECT
    t.tenant_id,
    'OrganizationUnitType',
    dt.type_name,
    'system',
    0,
    now(),
    now(),
    'system',
    1,
    1,
    jsonb_build_object(
        'type_code', dt.type_code,
        'type_name', dt.type_name,
        'hierarchy_order', dt.hierarchy_order,
        'parent_type', dt.parent_type,
        'is_active', true,
        'is_system', true,
        'description', dt.description
    )
FROM tenants t
CROSS JOIN default_types dt
ON CONFLICT (tenant_id, doctype, name) DO NOTHING;

-- 2. Backfill base_unit_code, status, materialized_path, depth, and breadcrumb on existing OrganizationUnit records
WITH RECURSIVE org_tree AS (
    SELECT
        d.tenant_id,
        d.name,
        COALESCE(NULLIF(d.data ->> 'org_unit_code', ''), d.name) AS code,
        COALESCE(NULLIF(d.data ->> 'org_unit_name', ''), d.name) AS title,
        1 AS depth,
        '/' || COALESCE(NULLIF(d.data ->> 'org_unit_code', ''), d.name) || '/' AS mpath,
        COALESCE(NULLIF(d.data ->> 'org_unit_name', ''), d.name) AS crumb,
        ARRAY[d.name] AS visited
    FROM frogframe_documents d
    WHERE d.doctype = 'OrganizationUnit'
      AND (d.data ->> 'parent_org_unit' IS NULL OR d.data ->> 'parent_org_unit' = '')

    UNION ALL

    SELECT
        c.tenant_id,
        c.name,
        COALESCE(NULLIF(c.data ->> 'org_unit_code', ''), c.name) AS code,
        COALESCE(NULLIF(c.data ->> 'org_unit_name', ''), c.name) AS title,
        p.depth + 1 AS depth,
        p.mpath || COALESCE(NULLIF(c.data ->> 'org_unit_code', ''), c.name) || '/' AS mpath,
        p.crumb || ' > ' || COALESCE(NULLIF(c.data ->> 'org_unit_name', ''), c.name) AS crumb,
        p.visited || c.name
    FROM frogframe_documents c
    JOIN org_tree p
      ON c.tenant_id = p.tenant_id
     AND c.doctype = 'OrganizationUnit'
     AND (c.data ->> 'parent_org_unit') = p.name
    WHERE NOT (c.name = ANY(p.visited))
      AND p.depth < 30
)
UPDATE frogframe_documents u
SET data = u.data
    || jsonb_build_object(
        'base_unit_code', COALESCE(NULLIF(u.data ->> 'base_unit_code', ''), t.code),
        'status', COALESCE(NULLIF(u.data ->> 'status', ''), CASE WHEN COALESCE((u.data ->> 'is_active')::boolean, true) THEN 'Active' ELSE 'Closed' END),
        'depth', t.depth,
        'materialized_path', t.mpath,
        'breadcrumb', t.crumb
    )
FROM org_tree t
WHERE u.tenant_id = t.tenant_id
  AND u.doctype = 'OrganizationUnit'
  AND u.name = t.name;

-- Backfill Employee and Position cached organization_breadcrumb and organization_path from OrganizationUnit
UPDATE frogframe_documents e
SET data = e.data
    || jsonb_build_object(
        'organization_breadcrumb', u.data ->> 'breadcrumb',
        'organization_path', u.data ->> 'materialized_path'
    )
FROM frogframe_documents u
WHERE e.doctype IN ('Employee', 'Position')
  AND u.doctype = 'OrganizationUnit'
  AND e.tenant_id = u.tenant_id
  AND (e.data ->> 'organization_unit') = u.name
  AND (u.data ->> 'materialized_path') IS NOT NULL;

-- 3. Specialized indexes for OrganizationUnit effective dating, prefix path search, and WSM daily schedule lookup
CREATE INDEX IF NOT EXISTS ix_erp_org_unit_validity
    ON frogframe_documents (tenant_id, (data ->> 'base_unit_code'), (data ->> 'effective_from'), (data ->> 'effective_to'))
    WHERE doctype = 'OrganizationUnit';

CREATE INDEX IF NOT EXISTS ix_erp_org_unit_path_prefix
    ON frogframe_documents (tenant_id, (data ->> 'materialized_path') text_pattern_ops)
    WHERE doctype = 'OrganizationUnit';

CREATE INDEX IF NOT EXISTS ix_erp_org_unit_parent_valid
    ON frogframe_documents (tenant_id, (data ->> 'parent_org_unit'), (data ->> 'effective_from'))
    WHERE doctype = 'OrganizationUnit';

CREATE INDEX IF NOT EXISTS ix_erp_daily_schedule_emp_date
    ON frogframe_documents (tenant_id, (data ->> 'employee'), (data ->> 'work_date'))
    WHERE doctype = 'EmployeeDailySchedule';

CREATE INDEX IF NOT EXISTS ix_erp_daily_schedule_unit_date
    ON frogframe_documents (tenant_id, (data ->> 'organization_unit'), (data ->> 'work_date'))
    WHERE doctype = 'EmployeeDailySchedule';
