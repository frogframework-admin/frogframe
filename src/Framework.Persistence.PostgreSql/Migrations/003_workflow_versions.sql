-- DocFrame: versi workflow hasil publish (immutable, satu versi aktif per workflow).
CREATE TABLE docframe_workflow_versions (
    name         text        NOT NULL,
    doctype      text        NOT NULL,
    version      integer     NOT NULL,
    definition   jsonb       NOT NULL,
    status       text        NOT NULL,
    published_by text        NOT NULL,
    published_at timestamptz NOT NULL,
    changes      jsonb       NOT NULL DEFAULT '[]',
    PRIMARY KEY (name, version)
);
CREATE UNIQUE INDEX ux_workflow_one_published ON docframe_workflow_versions (name) WHERE status = 'Published';
