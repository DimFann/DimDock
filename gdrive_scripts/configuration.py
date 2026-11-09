import os
from pathlib import Path

from clean import read_settings


ROOT_FOLDER_ID = 'GOOGLE_DRIVE_ROOT_FOLDER_ID'


def drive_settings() -> dict[str, str]:
    path = Path(__file__).resolve().parent.parent / '.env'
    settings = read_settings(path) if path.exists() else {}
    keys = ('GOOGLE_DRIVE_API_KEY', 'GOOGLE_DRIVE_ROOT_RESOURCE_KEY', ROOT_FOLDER_ID)
    return {key: os.environ.get(key, settings.get(key, '')).strip() for key in keys}


def root_folder_id(argument: str | None, settings: dict[str, str]) -> str:
    """--root-folder-id wins; otherwise GOOGLE_DRIVE_ROOT_FOLDER_ID from the environment or .env."""
    return (argument or '').strip() or settings.get(ROOT_FOLDER_ID, '')
