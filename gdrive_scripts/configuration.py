import os
from pathlib import Path

from clean import read_settings


def drive_settings() -> dict[str, str]:
    path = Path(__file__).resolve().parent.parent / '.env'
    settings = read_settings(path) if path.exists() else {}
    keys = ('GOOGLE_DRIVE_API_KEY', 'GOOGLE_DRIVE_ROOT_RESOURCE_KEY')
    return {key: os.environ.get(key, settings.get(key, '')).strip() for key in keys}
