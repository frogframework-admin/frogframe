-- FrogFrame: indeks untuk retensi outbox (penghapusan pesan yang sudah diproses, per batch).
CREATE INDEX IF NOT EXISTS ix_outbox_processed ON frogframe_outbox (processed_at)
    WHERE processed_at IS NOT NULL AND NOT dead_lettered;
