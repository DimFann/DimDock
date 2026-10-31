-- Enable strict mode and JSON support
SET sql_mode = 'STRICT_ALL_TABLES';

-- Create LegacyIdMap table
CREATE TABLE LegacyIdMap (
    legacy_id VARCHAR(255) PRIMARY KEY,
    redirect_id BIGINT,
    type VARCHAR(32)
);

-- Create Items table with auto-incrementing id
CREATE TABLE Items (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY, -- Unique identifier (PK)
    original_name VARCHAR(255) NOT NULL, -- Original file name
    parent_path VARCHAR(1024), -- Original full parent path e.g. /2025/2025-10
    description TEXT, -- Markdown description
    uploaded DATETIME, -- Date uploaded
    created DATETIME, -- Date created
    display_index INT, -- Display order override
    type VARCHAR(32), -- Type of item, image, text, video, etc.
    metadata JSON -- Metadata, unused currently
) AUTO_INCREMENT = 0;

CREATE TABLE Shortcuts (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY, -- Unique identifier (PK)
    original_id BIGINT UNSIGNED NOT NULL, -- Unique identifier of original item.
    original_name VARCHAR(255) NOT NULL, -- Original file name
    parent_path VARCHAR(1024), -- Original full parent path e.g. /2025/2025-10
    shortcut_path VARCHAR(1024), -- Location where this item will appear, in addition to actual location.
    CONSTRAINT fk_shortcuts_original
        FOREIGN KEY (original_id) REFERENCES Items(id)
        ON DELETE CASCADE
) AUTO_INCREMENT = 0;


-- Create Paths table with auto-incrementing id
CREATE TABLE Paths (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    path VARCHAR(1024) NOT NULL,
    parent_path VARCHAR(1024),
    description TEXT,
    type VARCHAR(32),
    metadata JSON
) AUTO_INCREMENT = 0;
