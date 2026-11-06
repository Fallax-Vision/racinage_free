-- Racinage Free local family tree. Additive and idempotent.
-- Mirrors hosted family_trees, persons, relationships, parent_child, family_tree_person_settings,
-- family_tree_partial_views and person_gallery_media, minus hosted-only columns
-- (family_long_id, collaborators, creator/updater users, public visibility).
-- Mutable data is created only in %LOCALAPPDATA%\Racinage Free\data\racinage-free.sqlite.
-- LocalStore.Initialize runs this same script (LocalFamilyTreeSchema.Sql) on every start.
BEGIN IMMEDIATE;

CREATE TABLE IF NOT EXISTS family_trees (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  long_id TEXT NOT NULL UNIQUE,
  name TEXT NOT NULL,
  description TEXT NULL,
  root_person_id INTEGER NULL REFERENCES persons(id) ON DELETE SET NULL,
  primary_relationship_id INTEGER NULL REFERENCES relationships(id) ON DELETE SET NULL,
  status TEXT NOT NULL DEFAULT 'active' CHECK(status IN ('active', 'archived', 'deleted')),
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS persons (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  long_id TEXT NOT NULL UNIQUE,
  first_name TEXT NULL,
  last_name TEXT NULL,
  birth_date TEXT NULL,
  death_date TEXT NULL,
  is_deceased INTEGER NOT NULL DEFAULT 0 CHECK(is_deceased IN (0, 1)),
  gender TEXT NULL CHECK(gender IS NULL OR gender IN ('M', 'F')),
  image_name TEXT NULL,
  phone1 TEXT NULL,
  phone2 TEXT NULL,
  email1 TEXT NULL,
  email2 TEXT NULL,
  address TEXT NULL,
  religion TEXT NULL,
  website TEXT NULL,
  visibility TEXT NOT NULL DEFAULT 'visible' CHECK(visibility IN ('visible', 'hidden')),
  birth_place TEXT NULL,
  nationality TEXT NULL,
  blood_group TEXT NULL,
  marital_status TEXT NOT NULL DEFAULT 'single' CHECK(marital_status IN ('single', 'married', 'divorced', 'widowed')),
  education_level TEXT NULL,
  migration_note TEXT NULL,
  legacy_people_id INTEGER NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS relationships (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  family_tree_id INTEGER NOT NULL REFERENCES family_trees(id) ON DELETE CASCADE,
  person1_id INTEGER NOT NULL REFERENCES persons(id) ON DELETE CASCADE,
  person2_id INTEGER NOT NULL REFERENCES persons(id) ON DELETE CASCADE,
  relationship_type TEXT NOT NULL CHECK(relationship_type IN ('spouse', 'partner')),
  start_date TEXT NULL,
  end_date TEXT NULL,
  is_current INTEGER NOT NULL DEFAULT 1 CHECK(is_current IN (0, 1)),
  sibling_person_id INTEGER NULL,
  created_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_rel_tree_type_p1_p2 ON relationships(family_tree_id, relationship_type, person1_id, person2_id);
CREATE INDEX IF NOT EXISTS idx_rel_tree_type_p2_p1 ON relationships(family_tree_id, relationship_type, person2_id, person1_id);

CREATE TABLE IF NOT EXISTS parent_child (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  family_tree_id INTEGER NOT NULL REFERENCES family_trees(id) ON DELETE CASCADE,
  child_id INTEGER NOT NULL REFERENCES persons(id) ON DELETE CASCADE,
  parent_id INTEGER NOT NULL REFERENCES persons(id) ON DELETE CASCADE,
  created_at TEXT NOT NULL,
  UNIQUE(family_tree_id, parent_id, child_id)
);
CREATE INDEX IF NOT EXISTS idx_pc_tree_child_parent ON parent_child(family_tree_id, child_id, parent_id);

CREATE TABLE IF NOT EXISTS family_tree_person_settings (
  family_tree_id INTEGER NOT NULL REFERENCES family_trees(id) ON DELETE CASCADE,
  person_id INTEGER NOT NULL REFERENCES persons(id) ON DELETE CASCADE,
  sibling_birth_order INTEGER NULL CHECK(sibling_birth_order IS NULL OR sibling_birth_order BETWEEN 1 AND 65535),
  preferred_relationship_id INTEGER NULL REFERENCES relationships(id) ON DELETE SET NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  PRIMARY KEY(family_tree_id, person_id)
);
CREATE INDEX IF NOT EXISTS idx_family_tree_person_birth_order ON family_tree_person_settings(family_tree_id, sibling_birth_order);

CREATE TABLE IF NOT EXISTS family_tree_partial_views (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  long_id TEXT NOT NULL UNIQUE,
  family_tree_id INTEGER NOT NULL REFERENCES family_trees(id) ON DELETE CASCADE,
  anchor_relationship_id INTEGER NOT NULL,
  anchor_person_id INTEGER NOT NULL,
  anchor_partner_id INTEGER NULL,
  name TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'active' CHECK(status IN ('active', 'disabled', 'archived', 'deleted')),
  disabled_reason TEXT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  archived_at TEXT NULL,
  deleted_at TEXT NULL
);
CREATE INDEX IF NOT EXISTS idx_family_tree_partial_anchor ON family_tree_partial_views(family_tree_id, anchor_relationship_id);

CREATE TABLE IF NOT EXISTS person_gallery_media (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  long_id TEXT NOT NULL UNIQUE,
  person_id INTEGER NOT NULL REFERENCES persons(id) ON DELETE CASCADE,
  media_type TEXT NOT NULL CHECK(media_type IN ('image', 'video')),
  file_name TEXT NOT NULL,
  original_name TEXT NULL,
  caption TEXT NULL,
  mime_type TEXT NOT NULL,
  file_size INTEGER NOT NULL DEFAULT 0,
  width INTEGER NOT NULL DEFAULT 0,
  height INTEGER NOT NULL DEFAULT 0,
  status TEXT NOT NULL DEFAULT 'active' CHECK(status IN ('active', 'deleted')),
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_person_gallery_person ON person_gallery_media(person_id, status, created_at);

-- Legacy flat people list. Each row is copied once, stays unplaced, and the people table is kept for rollback.
-- The map survives later deletion of the migrated person, so a rerun never brings it back.
CREATE TABLE IF NOT EXISTS local_legacy_people_migrations (
  people_id INTEGER PRIMARY KEY,
  person_id INTEGER NOT NULL,
  migrated_at TEXT NOT NULL
);

INSERT INTO persons (long_id, first_name, last_name, birth_date, birth_place, migration_note, legacy_people_id, created_at, updated_at)
SELECT 'local_person_' || lower(hex(randomblob(16))),
  CASE WHEN instr(n, ' ') = 0 THEN n ELSE rtrim(rtrim(n, replace(n, ' ', ''))) END,
  CASE WHEN instr(n, ' ') = 0 THEN NULL ELSE substr(n, length(rtrim(n, replace(n, ' ', ''))) + 1) END,
  nullif(trim(birth_date), ''),
  nullif(trim(place), ''),
  nullif(
    CASE WHEN trim(relationship) <> '' THEN 'Legacy relationship: ' || trim(relationship) ELSE '' END
    || CASE WHEN trim(relationship) <> '' AND trim(notes) <> '' THEN char(10) ELSE '' END
    || CASE WHEN trim(notes) <> '' THEN 'Legacy notes: ' || trim(notes) ELSE '' END, ''),
  id, created_at, updated_at
FROM (SELECT id, trim(full_name) AS n, relationship, birth_date, place, notes, created_at, updated_at
      FROM people p
      WHERE deleted_at IS NULL
        AND NOT EXISTS (SELECT 1 FROM local_legacy_people_migrations m WHERE m.people_id = p.id)
      ORDER BY id);

INSERT OR IGNORE INTO local_legacy_people_migrations (people_id, person_id, migrated_at)
SELECT legacy_people_id, id, strftime('%Y-%m-%dT%H:%M:%SZ', 'now') FROM persons WHERE legacy_people_id IS NOT NULL;

COMMIT;
