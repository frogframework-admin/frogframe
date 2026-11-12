-- Translations entered by administrators per tenant (override the dictionaries shipped with the application).
CREATE TABLE IF NOT EXISTS frogframe_translations (
    tenant_id   TEXT        NOT NULL,
    language    VARCHAR(8)  NOT NULL,
    source      TEXT        NOT NULL,
    target      TEXT        NOT NULL,
    modified_by TEXT        NOT NULL DEFAULT '',
    modified    TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (tenant_id, language, source)
);
