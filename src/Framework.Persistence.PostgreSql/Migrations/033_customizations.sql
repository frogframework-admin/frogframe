-- Customization layer (Custom Field / Property Setter) for application DocTypes.
-- Every save is a new row; the highest version per DocType is active. An empty definition means "reset".
CREATE TABLE IF NOT EXISTS frogframe_customizations (
    doctype     TEXT        NOT NULL,
    version     INT         NOT NULL,
    definition  JSONB       NOT NULL,
    modified_by TEXT        NOT NULL,
    modified_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (doctype, version)
);
