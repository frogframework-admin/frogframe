CREATE TABLE IF NOT EXISTS docframe_data_protection_keys (
    id              serial          PRIMARY KEY,
    friendly_name   text            NOT NULL,
    xml             text            NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_data_protection_friendly_name 
    ON docframe_data_protection_keys (friendly_name);

