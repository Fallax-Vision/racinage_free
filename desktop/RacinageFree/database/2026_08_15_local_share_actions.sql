-- Additive portable schema for Share with Racinage and traditional Kitchen imports.
-- LocalStore.Initialize applies the same idempotent schema to the mutable database
-- under %LOCALAPPDATA%\Racinage Free. Prior signed release output is unchanged.
ALTER TABLE plugin_installs ADD COLUMN share_actions_json TEXT NOT NULL DEFAULT '';

CREATE TABLE IF NOT EXISTS local_share_receipts (
  receipt_id TEXT PRIMARY KEY,
  payload_kind TEXT NOT NULL CHECK(payload_kind IN ('url', 'text')),
  payload_text TEXT NOT NULL,
  normalized_url TEXT NOT NULL DEFAULT '',
  source TEXT NOT NULL,
  status TEXT NOT NULL CHECK(status IN ('pending', 'handled', 'dismissed', 'expired')),
  revision INTEGER NOT NULL DEFAULT 1,
  received_at TEXT NOT NULL,
  expires_at TEXT NOT NULL,
  handled_at TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_local_share_receipts_state
  ON local_share_receipts(status, received_at);

CREATE TABLE IF NOT EXISTS local_share_deliveries (
  receipt_id TEXT NOT NULL,
  plugin_slug TEXT NOT NULL,
  action_id TEXT NOT NULL,
  target_id TEXT NOT NULL DEFAULT '',
  status TEXT NOT NULL CHECK(status IN ('queued', 'completed', 'pending', 'failed')),
  idempotency_key TEXT NOT NULL UNIQUE,
  result_id TEXT NOT NULL DEFAULT '',
  error_message TEXT NOT NULL DEFAULT '',
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY(receipt_id, plugin_slug, action_id, target_id),
  FOREIGN KEY(receipt_id) REFERENCES local_share_receipts(receipt_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS local_kitchen_imports (
  import_id TEXT PRIMARY KEY,
  plugin_slug TEXT NOT NULL,
  workspace_long_id TEXT NOT NULL,
  receipt_id TEXT NOT NULL DEFAULT '',
  source_url TEXT NOT NULL,
  source_hash TEXT NOT NULL,
  status TEXT NOT NULL CHECK(status IN ('queued', 'extracting', 'completed', 'pending', 'failed', 'ignored_non_food', 'cancelled')),
  latest_mode TEXT NOT NULL DEFAULT 'web_scrape',
  result_recipe_long_id TEXT NOT NULL DEFAULT '',
  reason TEXT NOT NULL DEFAULT '',
  attempts INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_local_kitchen_import_queue
  ON local_kitchen_imports(status, created_at);

CREATE TABLE IF NOT EXISTS local_kitchen_extraction_runs (
  run_id TEXT PRIMARY KEY,
  import_id TEXT NOT NULL,
  mode TEXT NOT NULL CHECK(mode IN ('manual', 'web_scrape', 'ai')),
  status TEXT NOT NULL CHECK(status IN ('queued', 'running', 'completed', 'pending', 'failed', 'ignored_non_food')),
  confidence TEXT NOT NULL DEFAULT 'unknown',
  source_language TEXT NOT NULL DEFAULT 'und',
  provider_kind TEXT NOT NULL DEFAULT '',
  model TEXT NOT NULL DEFAULT '',
  evidence_json TEXT NOT NULL DEFAULT '{}',
  normalized_output_json TEXT NOT NULL DEFAULT '{}',
  reason TEXT NOT NULL DEFAULT '',
  created_at TEXT NOT NULL,
  completed_at TEXT NOT NULL DEFAULT '',
  FOREIGN KEY(import_id) REFERENCES local_kitchen_imports(import_id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_local_kitchen_runs_import
  ON local_kitchen_extraction_runs(import_id, created_at);
