-- Racinage Free 0.17.0 connected messaging cache and encrypted outbox.
-- The native host applies this schema idempotently on startup.

CREATE TABLE IF NOT EXISTS connected_account (
  id INTEGER PRIMARY KEY CHECK(id = 1),
  state TEXT NOT NULL DEFAULT 'disconnected',
  user_id TEXT NOT NULL DEFAULT '',
  access_expires_utc TEXT NOT NULL DEFAULT '',
  refresh_expires_utc TEXT NOT NULL DEFAULT '',
  event_cursor TEXT NOT NULL DEFAULT '',
  device_code_cipher TEXT NOT NULL DEFAULT '',
  user_code TEXT NOT NULL DEFAULT '',
  verification_url TEXT NOT NULL DEFAULT '',
  device_expires_utc TEXT NOT NULL DEFAULT '',
  quota_cipher TEXT NOT NULL DEFAULT '',
  last_sync_utc TEXT NOT NULL DEFAULT '',
  last_error TEXT NOT NULL DEFAULT '',
  updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS connected_conversations (
  long_id TEXT PRIMARY KEY,
  kind TEXT NOT NULL,
  state TEXT NOT NULL,
  title_cipher TEXT NOT NULL,
  revision INTEGER NOT NULL DEFAULT 1,
  last_sequence INTEGER NOT NULL DEFAULT 0,
  read_sequence INTEGER NOT NULL DEFAULT 0,
  unread_count INTEGER NOT NULL DEFAULT 0,
  muted INTEGER NOT NULL DEFAULT 0,
  archived INTEGER NOT NULL DEFAULT 0,
  updated_at_utc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS connected_messages (
  long_id TEXT PRIMARY KEY,
  conversation_long_id TEXT NOT NULL,
  sequence_no INTEGER NOT NULL,
  sender_cipher TEXT NOT NULL,
  message_type TEXT NOT NULL,
  root_message_long_id TEXT NOT NULL DEFAULT '',
  body_cipher TEXT NOT NULL,
  metadata_cipher TEXT NOT NULL,
  edited_at_utc TEXT NOT NULL DEFAULT '',
  deleted_at_utc TEXT NOT NULL DEFAULT '',
  created_at_utc TEXT NOT NULL,
  UNIQUE(conversation_long_id, sequence_no)
);

CREATE INDEX IF NOT EXISTS idx_connected_messages_conversation
  ON connected_messages(conversation_long_id, sequence_no);

CREATE TABLE IF NOT EXISTS connected_outbox (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  long_id TEXT NOT NULL UNIQUE,
  conversation_long_id TEXT NOT NULL,
  item_kind TEXT NOT NULL,
  payload_cipher TEXT NOT NULL DEFAULT '',
  encrypted_file_path TEXT NOT NULL DEFAULT '',
  file_name_cipher TEXT NOT NULL DEFAULT '',
  mime_type TEXT NOT NULL DEFAULT '',
  file_size INTEGER NOT NULL DEFAULT 0,
  sha256 TEXT NOT NULL DEFAULT '',
  upload_id TEXT NOT NULL DEFAULT '',
  attachment_id TEXT NOT NULL DEFAULT '',
  upload_offset INTEGER NOT NULL DEFAULT 0,
  status TEXT NOT NULL DEFAULT 'queued',
  error_code TEXT NOT NULL DEFAULT '',
  created_at_utc TEXT NOT NULL,
  updated_at_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_connected_outbox_state
  ON connected_outbox(status, id);
