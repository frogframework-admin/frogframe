-- DocFrame: buku besar (general ledger) append-only. Spesifikasi akuntansi fase 1 V1.2, bagian 5.
CREATE TABLE docframe_gl_entries (
    id                    uuid          PRIMARY KEY,
    tenant_id             text          NOT NULL,
    seq                   bigint        NOT NULL,
    posting_date          date          NOT NULL,
    period_id             text          NOT NULL,
    account_id            text          NOT NULL,
    debit                 numeric(18,2) NOT NULL DEFAULT 0 CHECK (debit  >= 0),
    credit                numeric(18,2) NOT NULL DEFAULT 0 CHECK (credit >= 0),
    voucher_type          text          NOT NULL,
    voucher_name          text          NOT NULL,
    voucher_version       int           NOT NULL,
    line_number           int           NOT NULL,
    party_type            text,
    party                 text,
    against_voucher_type  text,
    against_voucher_name  text,
    department_id         text,
    remarks               text,
    reversal_of           uuid          REFERENCES docframe_gl_entries(id),
    prev_hash             char(64)      NOT NULL,
    entry_hash            char(64)      NOT NULL,
    created_by            text          NOT NULL,
    created_at            timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT ck_gl_one_side       CHECK ((debit > 0) <> (credit > 0)),
    CONSTRAINT uq_gl_voucher_line   UNIQUE (tenant_id, voucher_type, voucher_name, voucher_version, line_number),
    CONSTRAINT uq_gl_seq            UNIQUE (tenant_id, seq),
    CONSTRAINT uq_gl_reversal       UNIQUE (reversal_of)
);
CREATE INDEX ix_gl_account_date ON docframe_gl_entries (tenant_id, account_id, posting_date);
CREATE INDEX ix_gl_voucher      ON docframe_gl_entries (tenant_id, voucher_type, voucher_name);
CREATE INDEX ix_gl_against      ON docframe_gl_entries (tenant_id, against_voucher_type, against_voucher_name) WHERE against_voucher_name IS NOT NULL;
CREATE INDEX ix_gl_party        ON docframe_gl_entries (tenant_id, party_type, party, posting_date) WHERE party IS NOT NULL;

-- Lapis 1 append-only: UPDATE, DELETE, dan TRUNCATE ditolak, juga untuk pemilik tabel.
-- (Lapis 2 = pemisahan peran database owner/app; perubahan deployment, lihat deploy/sql/gl-roles.sql.)
CREATE FUNCTION docframe_gl_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'docframe_gl: buku besar append-only; % tidak diizinkan. Koreksi dengan jurnal pembalik.', TG_OP;
END $$;
CREATE TRIGGER trg_gl_no_update_delete BEFORE UPDATE OR DELETE ON docframe_gl_entries
    FOR EACH ROW EXECUTE FUNCTION docframe_gl_append_only();
CREATE TRIGGER trg_gl_no_truncate BEFORE TRUNCATE ON docframe_gl_entries
    FOR EACH STATEMENT EXECUTE FUNCTION docframe_gl_append_only();

-- Keseimbangan per voucher diperiksa saat COMMIT (constraint trigger deferred), bukan hanya oleh aplikasi.
CREATE FUNCTION docframe_gl_balanced() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE d numeric; c numeric;
BEGIN
    SELECT COALESCE(SUM(debit), 0), COALESCE(SUM(credit), 0) INTO d, c FROM docframe_gl_entries
     WHERE tenant_id = NEW.tenant_id AND voucher_type = NEW.voucher_type AND voucher_name = NEW.voucher_name AND voucher_version = NEW.voucher_version;
    IF d <> c THEN
        RAISE EXCEPTION 'docframe_gl: jurnal % % tidak seimbang (debit %, kredit %)', NEW.voucher_type, NEW.voucher_name, d, c;
    END IF;
    RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER trg_gl_balanced AFTER INSERT ON docframe_gl_entries
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION docframe_gl_balanced();
