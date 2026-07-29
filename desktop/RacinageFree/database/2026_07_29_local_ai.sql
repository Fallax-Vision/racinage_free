-- Racinage Free v0.16.0 additive local AI schema.
-- Ciphertext fields are protected for the current Windows user before storage.
-- These tables are local-only and must never enter the hosted sync journal.

CREATE TABLE IF NOT EXISTS local_ai_conversations (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  long_id TEXT NOT NULL UNIQUE,
  title_ciphertext TEXT NOT NULL,
  title_key_version INTEGER NOT NULL DEFAULT 1,
  is_pinned INTEGER NOT NULL DEFAULT 0,
  is_archived INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  deleted_at TEXT NULL
);
CREATE INDEX IF NOT EXISTS idx_local_ai_conversations_state
  ON local_ai_conversations(is_archived, is_pinned, updated_at);

CREATE TABLE IF NOT EXISTS local_ai_messages (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  conversation_id INTEGER NOT NULL,
  role TEXT NOT NULL CHECK(role IN ('user', 'assistant', 'tool')),
  content_ciphertext TEXT NOT NULL,
  content_key_version INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL,
  deleted_at TEXT NULL,
  FOREIGN KEY(conversation_id) REFERENCES local_ai_conversations(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_local_ai_messages_conversation
  ON local_ai_messages(conversation_id, id);

CREATE TABLE IF NOT EXISTS local_ai_runs (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  long_id TEXT NOT NULL UNIQUE,
  conversation_id INTEGER NOT NULL,
  status TEXT NOT NULL CHECK(status IN ('queued', 'running', 'paused', 'completed', 'failed', 'cancelled')),
  provider_kind TEXT NOT NULL,
  model TEXT NOT NULL DEFAULT '',
  state_ciphertext TEXT NOT NULL,
  state_key_version INTEGER NOT NULL DEFAULT 1,
  correlation_id TEXT NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  FOREIGN KEY(conversation_id) REFERENCES local_ai_conversations(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_local_ai_runs_state
  ON local_ai_runs(status, updated_at);

CREATE TABLE IF NOT EXISTS local_ai_tool_calls (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  run_id INTEGER NOT NULL,
  tool_name TEXT NOT NULL,
  tier INTEGER NOT NULL CHECK(tier BETWEEN 0 AND 3),
  arguments_ciphertext TEXT NOT NULL,
  result_ciphertext TEXT NOT NULL DEFAULT '',
  payload_key_version INTEGER NOT NULL DEFAULT 1,
  status TEXT NOT NULL,
  idempotency_key TEXT NOT NULL UNIQUE,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  FOREIGN KEY(run_id) REFERENCES local_ai_runs(id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_local_ai_tool_calls_run
  ON local_ai_tool_calls(run_id, id);

CREATE TABLE IF NOT EXISTS local_ai_usage_audits (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  correlation_id TEXT NOT NULL,
  provider_kind TEXT NOT NULL,
  model TEXT NOT NULL DEFAULT '',
  tool_name TEXT NOT NULL DEFAULT '',
  confirmation_outcome TEXT NOT NULL DEFAULT '',
  status TEXT NOT NULL,
  latency_ms INTEGER NOT NULL DEFAULT 0,
  input_tokens INTEGER NOT NULL DEFAULT 0,
  output_tokens INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_local_ai_usage_audits_created
  ON local_ai_usage_audits(created_at);
