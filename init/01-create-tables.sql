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
    thumbnail_key CHAR(64) NOT NULL,
    description TEXT, -- Markdown description
    uploaded DATETIME, -- Date uploaded
    created DATETIME, -- Date created
    display_index INT, -- Display order override
    type VARCHAR(32), -- Type of item, image, text, video, etc.
    metadata JSON, -- Metadata, unused currently
    UNIQUE KEY uq_items_thumbnail_key (thumbnail_key)
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

CREATE TABLE Tags (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    UNIQUE KEY uq_tags_name (name)
) AUTO_INCREMENT = 0;

CREATE TABLE DirectoryShortcuts (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    target_path_id BIGINT UNSIGNED NOT NULL,
    parent_path_id BIGINT UNSIGNED NOT NULL,
    name VARCHAR(255) NOT NULL,
    CONSTRAINT fk_directory_shortcuts_target
        FOREIGN KEY (target_path_id) REFERENCES Paths(id) ON DELETE CASCADE,
    CONSTRAINT fk_directory_shortcuts_parent
        FOREIGN KEY (parent_path_id) REFERENCES Paths(id) ON DELETE CASCADE
);

CREATE TABLE ItemTags (
    item_id BIGINT UNSIGNED NOT NULL,
    tag_id BIGINT UNSIGNED NOT NULL,
    PRIMARY KEY (item_id, tag_id),
    CONSTRAINT fk_item_tags_item
        FOREIGN KEY (item_id) REFERENCES Items(id) ON DELETE CASCADE,
    CONSTRAINT fk_item_tags_tag
        FOREIGN KEY (tag_id) REFERENCES Tags(id) ON DELETE CASCADE,
    KEY ix_item_tags_tag (tag_id)
);

CREATE TABLE PathTags (
    path_id BIGINT UNSIGNED NOT NULL,
    tag_id BIGINT UNSIGNED NOT NULL,
    PRIMARY KEY (path_id, tag_id),
    CONSTRAINT fk_path_tags_path
        FOREIGN KEY (path_id) REFERENCES Paths(id) ON DELETE CASCADE,
    CONSTRAINT fk_path_tags_tag
        FOREIGN KEY (tag_id) REFERENCES Tags(id) ON DELETE CASCADE,
    KEY ix_path_tags_tag (tag_id)
);

CREATE TABLE PendingPathDeletions (
    path VARCHAR(1024) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY
);

CREATE TABLE PendingItemDeletions (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    parent_path VARCHAR(1024) NOT NULL,
    original_name VARCHAR(255) NOT NULL
);

CREATE TABLE AdminOnlyTagSettings (
    id TINYINT UNSIGNED NOT NULL PRIMARY KEY,
    tags JSON NOT NULL
);

CREATE TABLE Blogs (
    id BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
    source_filename VARCHAR(255) NOT NULL,
    alias VARCHAR(255) NOT NULL,
    title VARCHAR(1024),
    timestamp DATETIME,
    `text` TEXT,
    UNIQUE KEY uq_blogs_source_filename (source_filename),
    UNIQUE KEY uq_blogs_alias (alias)
) AUTO_INCREMENT = 0;

-- Create Paths table with auto-incrementing id
CREATE TABLE About (
    id BIGINT UNSIGNED NOT NULL PRIMARY KEY,
    description TEXT
) AUTO_INCREMENT = 0;