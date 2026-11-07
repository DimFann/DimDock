@echo off
setlocal

set "CLEAR_THUMBS="
if /I "%~1"=="/thumbs" set "CLEAR_THUMBS=1"
if /I "%~1"=="--thumbs" set "CLEAR_THUMBS=1"
if not "%~1"=="" if not defined CLEAR_THUMBS (
    echo Usage: %~nx0 [/thumbs]
    exit /b 2
)
if not "%~2"=="" (
    echo Usage: %~nx0 [/thumbs]
    exit /b 2
)

set "SCRIPT_DIR=%~dp0"
set "DATA_DIR="

if not exist "%SCRIPT_DIR%.env" (
    echo Missing "%SCRIPT_DIR%.env". Set DATA_DIR to an absolute path.
    exit /b 1
)

for /f "usebackq eol=# tokens=1,* delims==" %%A in ("%SCRIPT_DIR%.env") do (
    if "%%A"=="DATA_DIR" set "DATA_DIR=%%B"
)

if not defined DATA_DIR (
    echo DATA_DIR is missing from "%SCRIPT_DIR%.env".
    exit /b 1
)

set "DATA_DIR=%DATA_DIR:/=\%"
if not "%DATA_DIR:~1,2%"==":\" if not "%DATA_DIR:~0,2%"=="\\" (
    echo DATA_DIR must be an absolute Windows path: "%DATA_DIR%"
    exit /b 1
)

if not exist "%DATA_DIR%\" (
    echo Data directory does not exist: "%DATA_DIR%"
    exit /b 1
)

echo Clearing app_state and db_data...
call :clean "%DATA_DIR%\app_state"
if errorlevel 1 exit /b 1
call :clean "%DATA_DIR%\db_data"
if errorlevel 1 exit /b 1

if defined CLEAR_THUMBS (
    call :clean "%DATA_DIR%\img_db_thumbs"
    if errorlevel 1 exit /b 1
) else (
    echo Keeping "%DATA_DIR%\img_db_thumbs". Use %~nx0 /thumbs to clear it.
)

echo Done.
exit /b 0

:clean
if not exist "%~1\" (
    echo Directory does not exist: "%~1"
    exit /b 1
)

echo Cleaning "%~1"...
del /f /s /q /a "%~1\*" >nul 2>&1
for /d %%D in ("%~1\*") do rd /s /q "%%~fD"
exit /b 0
