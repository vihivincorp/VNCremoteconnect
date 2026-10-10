@echo off
cd /d "%~dp0"
if exist "dist\asynchronous\asynchronous.exe" (
    start "" "dist\asynchronous\asynchronous.exe"
) else if exist "dist\VNCremoteconnect\asynchronous.exe" (
    start "" "dist\VNCremoteconnect\asynchronous.exe"
) else (
    start "" "dist\VNCremoteconnect\VNCremoteconnect.exe"
)
