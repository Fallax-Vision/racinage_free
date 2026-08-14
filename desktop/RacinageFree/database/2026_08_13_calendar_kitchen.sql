-- Additive local schema for Calendar and reviewed Kitchen Planner bridge data.
-- Mutable records remain under %LOCALAPPDATA% at runtime. This file documents
-- the schema added by LocalStore.Initialize and does not alter prior releases.
CREATE TABLE IF NOT EXISTS local_calendar_items (
  long_id TEXT PRIMARY KEY,
  source_id TEXT NOT NULL DEFAULT 'core.calendar',
  source_opaque_id TEXT NOT NULL DEFAULT '',
  item_kind TEXT NOT NULL,
  title TEXT NOT NULL,
  start_utc TEXT NOT NULL DEFAULT '',
  end_utc TEXT NOT NULL DEFAULT '',
  date_value TEXT NOT NULL DEFAULT '',
  timezone TEXT NOT NULL DEFAULT 'UTC',
  all_day INTEGER NOT NULL DEFAULT 0,
  status TEXT NOT NULL DEFAULT 'planned',
  recurrence_json TEXT NOT NULL DEFAULT '',
  reminder_json TEXT NOT NULL DEFAULT '',
  color TEXT NOT NULL DEFAULT '#0f7370',
  notes TEXT NOT NULL DEFAULT '',
  revision INTEGER NOT NULL DEFAULT 1,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_local_calendar_range ON local_calendar_items(status,date_value,start_utc);
CREATE UNIQUE INDEX IF NOT EXISTS uniq_local_calendar_source ON local_calendar_items(source_id,source_opaque_id) WHERE source_opaque_id<>'';
CREATE TABLE IF NOT EXISTS local_calendar_exceptions (
  item_long_id TEXT NOT NULL,
  occurrence_key TEXT NOT NULL,
  action TEXT NOT NULL,
  override_json TEXT NOT NULL DEFAULT '',
  created_at TEXT NOT NULL,
  PRIMARY KEY(item_long_id,occurrence_key)
);
CREATE TABLE IF NOT EXISTS local_calendar_preferences (
  id INTEGER PRIMARY KEY CHECK(id=1),
  view_name TEXT NOT NULL DEFAULT 'month',
  anchor_date TEXT NOT NULL DEFAULT '',
  filters_json TEXT NOT NULL DEFAULT '{}',
  working_hours_json TEXT NOT NULL DEFAULT '{}',
  updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS local_calendar_ics_feeds (
  long_id TEXT PRIMARY KEY,
  source_name TEXT NOT NULL,
  source_fingerprint TEXT NOT NULL,
  imported_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS local_calendar_reminder_claims (
  item_long_id TEXT NOT NULL,
  occurrence_key TEXT NOT NULL,
  reminder_offset_minutes INTEGER NOT NULL,
  delivered_at TEXT NOT NULL,
  PRIMARY KEY(item_long_id,occurrence_key,reminder_offset_minutes)
);
