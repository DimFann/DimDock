@echo off
echo Clearing contents of img_db, img_db_thumbs, and db_data...

for %%F in ("app_state" "img_db_thumbs" "db_data") do (
    echo Cleaning %%F...
    del /s /q "%%F\*.*"
    for /d %%D in ("%%F\*") do rd /s /q "%%D"
)

echo Done.
