@echo off
python "%~dp0clean.py" %*
exit /b %errorlevel%
