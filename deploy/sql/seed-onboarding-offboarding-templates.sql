-- Seed default OnboardingTemplate and OffboardingTemplate records for a tenant.
-- Usage: psql -v tenant=acme -v owner=admin -f deploy/sql/seed-onboarding-offboarding-templates.sql

-- ==================== ONBOARDING TEMPLATES ====================

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES (
    :'tenant',
    'OnboardingTemplate',
    'Standard Employee Onboarding',
    :'owner',
    0, NOW(), NOW(), :'owner', 1, 1,
    '{
      "template_name": "Standard Employee Onboarding",
      "is_active": true,
      "description": "General onboarding checklist for all new hires covering HR, IT, General Affairs, Manager, and Finance tasks.",
      "tasks": [
        {"idx": 1, "task": "Collect ID, tax ID (NPWP), bank account, and signed employment contract", "responsible": "HR", "due_days": 0, "notes": "Verify original documents during day-1 check-in"},
        {"idx": 2, "task": "Provision company email, SSO, and core system access", "responsible": "IT", "due_days": 0, "notes": "Send credentials to personal email or manager"},
        {"idx": 3, "task": "Prepare workstation, laptop, and office access card", "responsible": "General Affairs", "due_days": 0, "notes": "Log asset handover form"},
        {"idx": 4, "task": "Conduct company orientation, HR policy, and code of conduct briefing", "responsible": "HR", "due_days": 1, "notes": ""},
        {"idx": 5, "task": "Assign buddy/mentor and introduce team members", "responsible": "Manager", "due_days": 1, "notes": ""},
        {"idx": 6, "task": "Register employee for BPJS Employment & BPJS Health", "responsible": "HR", "due_days": 3, "notes": ""},
        {"idx": 7, "task": "Set 30-60-90 day goals and probation expectations", "responsible": "Manager", "due_days": 7, "notes": ""},
        {"idx": 8, "task": "Verify payroll and bank details for first salary run", "responsible": "Finance", "due_days": 14, "notes": ""}
      ]
    }'::jsonb
)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES (
    :'tenant',
    'OnboardingTemplate',
    'Engineering & Tech Onboarding',
    :'owner',
    0, NOW(), NOW(), :'owner', 1, 1,
    '{
      "template_name": "Engineering & Tech Onboarding",
      "is_active": true,
      "description": "Technical onboarding checklist for software engineers, QA, DevOps, and IT roles.",
      "tasks": [
        {"idx": 1, "task": "Collect signed NDA, employment contract, and tax/bank documents", "responsible": "HR", "due_days": 0, "notes": ""},
        {"idx": 2, "task": "Provision developer laptop, Git/CI-CD, cloud, and VPN access", "responsible": "IT", "due_days": 0, "notes": "Enforce MFA on all developer accounts"},
        {"idx": 3, "task": "Set up local development environment and repository access", "responsible": "Manager", "due_days": 1, "notes": ""},
        {"idx": 4, "task": "Security awareness & data privacy compliance training", "responsible": "IT", "due_days": 3, "notes": ""},
        {"idx": 5, "task": "Architecture & codebase walkthrough with tech lead", "responsible": "Manager", "due_days": 5, "notes": ""},
        {"idx": 6, "task": "Complete first starter ticket / pull request", "responsible": "Manager", "due_days": 14, "notes": ""},
        {"idx": 7, "task": "30-day probation check-in", "responsible": "HR", "due_days": 30, "notes": ""}
      ]
    }'::jsonb
)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES (
    :'tenant',
    'OnboardingTemplate',
    'Managerial & Leadership Onboarding',
    :'owner',
    0, NOW(), NOW(), :'owner', 1, 1,
    '{
      "template_name": "Managerial & Leadership Onboarding",
      "is_active": true,
      "description": "Onboarding checklist for team leads, managers, and department heads.",
      "tasks": [
        {"idx": 1, "task": "Complete managerial HR & compliance documentation", "responsible": "HR", "due_days": 0, "notes": ""},
        {"idx": 2, "task": "Provision laptop, email, and approval workflow permissions", "responsible": "IT", "due_days": 0, "notes": ""},
        {"idx": 3, "task": "Review department budget, headcount plan, and active KPIs", "responsible": "Finance", "due_days": 3, "notes": ""},
        {"idx": 4, "task": "1-on-1 alignment sessions with direct reports and peer managers", "responsible": "Manager", "due_days": 7, "notes": ""},
        {"idx": 5, "task": "HR management & leave/overtime approval training", "responsible": "HR", "due_days": 7, "notes": ""},
        {"idx": 6, "task": "Present 90-day department roadmap", "responsible": "Manager", "due_days": 30, "notes": ""}
      ]
    }'::jsonb
)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

-- ==================== OFFBOARDING TEMPLATES ====================

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES (
    :'tenant',
    'OffboardingTemplate',
    'Standard Employee Offboarding',
    :'owner',
    0, NOW(), NOW(), :'owner', 1, 1,
    '{
      "template_name": "Standard Employee Offboarding",
      "is_active": true,
      "description": "Standard exit clearance checklist for all departing employees.",
      "tasks": [
        {"idx": 1, "task": "Complete work & documentation handover to replacement or manager", "responsible": "Manager", "notes": "Ensure all active tasks and files are transferred"},
        {"idx": 2, "task": "Return company laptop, charger, ID badge, and access card", "responsible": "General Affairs", "notes": "Verify asset condition against asset register"},
        {"idx": 3, "task": "Revoke email, VPN, SSO, and all internal system access", "responsible": "IT", "notes": "Disable accounts on last working day"},
        {"idx": 4, "task": "Settle employee advances, expense claims, and final payroll deductions", "responsible": "Finance", "notes": "Confirm zero outstanding balance before final pay"},
        {"idx": 5, "task": "Conduct exit interview and issue employment certificate (Paklaring)", "responsible": "HR", "notes": "Archive signed exit form"},
        {"idx": 6, "task": "Deactivate BPJS Employment & BPJS Health membership", "responsible": "HR", "notes": "Process in next monthly BPJS cycle"}
      ]
    }'::jsonb
)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES (
    :'tenant',
    'OffboardingTemplate',
    'Resignation Offboarding',
    :'owner',
    0, NOW(), NOW(), :'owner', 1, 1,
    '{
      "template_name": "Resignation Offboarding",
      "separation_type": "Resignation",
      "is_active": true,
      "description": "Exit clearance checklist specifically for voluntary resignations.",
      "tasks": [
        {"idx": 1, "task": "Confirm resignation acceptance and notice period end date", "responsible": "HR", "notes": ""},
        {"idx": 2, "task": "Knowledge transfer & client/project handover", "responsible": "Manager", "notes": ""},
        {"idx": 3, "task": "Return company assets (laptop, access card, keys, uniform)", "responsible": "General Affairs", "notes": ""},
        {"idx": 4, "task": "Revoke system, email, and third-party SaaS access", "responsible": "IT", "notes": ""},
        {"idx": 5, "task": "Calculate final salary, unused leave encashment, and loan settlement", "responsible": "Finance", "notes": ""},
        {"idx": 6, "task": "Conduct exit interview & issue employment certificate", "responsible": "HR", "notes": ""},
        {"idx": 7, "task": "Process BPJS Employment & Health offboarding", "responsible": "HR", "notes": ""}
      ]
    }'::jsonb
)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;

INSERT INTO frogframe_documents (tenant_id, doctype, name, owner, docstatus, created, modified, modified_by, version, schema_version, data)
VALUES (
    :'tenant',
    'OffboardingTemplate',
    'Contract End Offboarding',
    :'owner',
    0, NOW(), NOW(), :'owner', 1, 1,
    '{
      "template_name": "Contract End Offboarding",
      "separation_type": "Contract end",
      "is_active": true,
      "description": "Exit clearance checklist for fixed-term contract (PKWT) completion.",
      "tasks": [
        {"idx": 1, "task": "Verify contract end date and non-renewal notice", "responsible": "HR", "notes": ""},
        {"idx": 2, "task": "Complete task & operational handover", "responsible": "Manager", "notes": ""},
        {"idx": 3, "task": "Collect company equipment and access cards", "responsible": "General Affairs", "notes": ""},
        {"idx": 4, "task": "Disable system and email accounts", "responsible": "IT", "notes": ""},
        {"idx": 5, "task": "Compute final pay and PKWT completion compensation (Uang Kompensasi)", "responsible": "Finance", "notes": "Per PP 35/2021"},
        {"idx": 6, "task": "Issue employment certificate & close BPJS coverage", "responsible": "HR", "notes": ""}
      ]
    }'::jsonb
)
ON CONFLICT (tenant_id, doctype, name) DO UPDATE
SET data = EXCLUDED.data, modified = NOW(), modified_by = EXCLUDED.modified_by;
