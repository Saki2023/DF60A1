PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;

CREATE TABLE IF NOT EXISTS accounts (
    account_id INTEGER PRIMARY KEY AUTOINCREMENT,
    account_name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    password TEXT NOT NULL DEFAULT '',
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_login_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS characters (
    character_id INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    job INTEGER NOT NULL,
    grow_type INTEGER NOT NULL DEFAULT 0,
    level INTEGER NOT NULL DEFAULT 1,
    town_id INTEGER NOT NULL DEFAULT 1,
    -- Town 1 area 1 is Elvengard/Gate.map (the Seria room).
    area_id INTEGER NOT NULL DEFAULT 1,
    pos_x INTEGER NOT NULL DEFAULT 474,
    -- Centre of Gate.map's [town movable area] (330,324,289,24).
    pos_y INTEGER NOT NULL DEFAULT 336,
    direction INTEGER NOT NULL DEFAULT 5,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY(account_id) REFERENCES accounts(account_id) ON DELETE CASCADE,
    UNIQUE(account_id, slot_index)
);

CREATE INDEX IF NOT EXISTS ix_characters_account_slot
    ON characters(account_id, slot_index);

-- Migrate the old value taken from Elvengard.twn's [gate] return point.
-- That point belongs to area 0 and is outside area 1's movable rectangle.
UPDATE characters
SET pos_y = 336, updated_at = CURRENT_TIMESTAMP
WHERE town_id = 1 AND area_id = 1 AND pos_x = 474 AND pos_y = 234;
