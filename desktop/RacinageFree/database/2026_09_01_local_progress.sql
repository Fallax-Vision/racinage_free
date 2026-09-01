-- Racinage Free local Progress analytics. Additive and idempotent.
-- Mutable data is created only in %LOCALAPPDATA%\Racinage Free\data\racinage-free.sqlite3.

CREATE TABLE IF NOT EXISTS local_progress_profile (
  id INTEGER PRIMARY KEY CHECK(id = 1),
  visual_mode TEXT NOT NULL DEFAULT 'subtle' CHECK(visual_mode IN ('background', 'subtle', 'expressive')),
  all_time_score INTEGER NOT NULL DEFAULT 0,
  current_streak INTEGER NOT NULL DEFAULT 0,
  longest_streak INTEGER NOT NULL DEFAULT 0,
  last_qualified_week TEXT NOT NULL DEFAULT '',
  level_code TEXT NOT NULL DEFAULT 'seed',
  updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS local_progress_events (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  event_key TEXT NOT NULL,
  source_type TEXT NOT NULL,
  source_id TEXT NOT NULL,
  idempotency_key TEXT NOT NULL UNIQUE,
  score_value INTEGER NOT NULL CHECK(score_value >= 0),
  occurred_at TEXT NOT NULL,
  week_start TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'awarded' CHECK(status IN ('awarded', 'suppressed'))
);

CREATE INDEX IF NOT EXISTS idx_local_progress_events_week
  ON local_progress_events(week_start, event_key, status);

CREATE TABLE IF NOT EXISTS local_progress_weekly_rollups (
  week_start TEXT PRIMARY KEY,
  score INTEGER NOT NULL DEFAULT 0,
  qualified INTEGER NOT NULL DEFAULT 0 CHECK(qualified IN (0, 1)),
  updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS local_progress_badges (
  badge_key TEXT PRIMARY KEY,
  awarded_at TEXT NOT NULL,
  source_event_id INTEGER NULL,
  FOREIGN KEY(source_event_id) REFERENCES local_progress_events(id) ON DELETE SET NULL
);
