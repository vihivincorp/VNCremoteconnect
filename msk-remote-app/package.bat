@echo off
setlocal
echo =========================================================
echo   Packaging MSK Remote Desktop Application (Release)
echo =========================================================

call build.bat
if %ERRORLEVEL% neq 0 (
    echo [ERROR] Build failed. Aborting package.
    exit /b 1
)

set DIST_DIR=..\dist\MSK-Remote
set ZIP_LIGHT=..\dist\MSK-Remote-v1.0.0-Lightweight.zip
set STANDALONE_DIR=..\dist\MSK-Remote-Standalone
set ZIP_STANDALONE=..\dist\MSK-Remote-v1.0.0-Standalone-with-Runtime.zip

if not exist ..\dist mkdir ..\dist
if not exist "%DIST_DIR%" mkdir "%DIST_DIR%"
if not exist "%DIST_DIR%\engine" mkdir "%DIST_DIR%\engine"
if not exist "%DIST_DIR%\vnc" mkdir "%DIST_DIR%\vnc"

echo Copying Application Binary...
copy /y bin\MSKRemote.exe "%DIST_DIR%\" >nul

echo Copying MSK E2EE Engine...
copy /y "..\msk-remote-poc\msk-engine.js" "%DIST_DIR%\engine\" >nul
copy /y "..\msk-remote-poc\msk-e2ee.js" "%DIST_DIR%\engine\" >nul
copy /y "..\msk-remote-poc\relay.pfx" "%DIST_DIR%\engine\" >nul

echo Copying TightVNC Binaries (GPL v2)...
if exist "..\tightvnc-bin\vncviewer.exe" (
    copy /y "..\tightvnc-bin\vncviewer.exe" "%DIST_DIR%\vnc\" >nul
    copy /y "..\tightvnc-bin\WinVNC.exe" "%DIST_DIR%\vnc\" >nul
    copy /y "..\tightvnc-bin\VNCHooks.dll" "%DIST_DIR%\vnc\" >nul
)
if exist "C:\Program Files\TightVNC\tvnviewer.exe" (
    copy /y "C:\Program Files\TightVNC\tvnviewer.exe" "%DIST_DIR%\vnc\" >nul
)


echo Creating Launcher Script...
(
echo @echo off
echo cd /d "%%~dp0"
echo start "" "MSKRemote.exe"
) > "%DIST_DIR%\Start-MSKRemote.bat"

echo Creating README and License Notices...
(
echo ===============================================================================
echo   MSK REMOTE - Windows Remote Desktop Application (v1.0.0)
echo ===============================================================================
echo.
echo MSK Remote enables secure, lightweight remote desktop connections across
echo different networks, firewalls, and college Wi-Fi without manual port forwarding.
echo.
echo KEY FEATURES:
echo - Zero-Knowledge Relay Architecture: Relay transfers opaque ciphertext frames.
echo - Authenticated End-to-End Encryption: X25519 ECDH + SHA-256 HKDF + AES-256-GCM.
echo - TightVNC Engine Integration: Maximum performance, low bandwidth consumption.
echo - Native Windows GUI: No Electron bloat, instantaneous launch.
echo.
echo QUICK START:
echo 1. Run 'MSKRemote.exe' or 'Start-MSKRemote.bat'.
echo 2. TO SHARE SCREEN:
echo    - Click 'Start Sharing'.
echo    - Give your 9-digit Device ID and Session PIN to your partner.
echo 3. TO CONNECT:
echo    - Enter partner's Device ID and Session PIN.
echo    - Click 'Connect to Remote PC'.
echo.
echo TIGHTVNC GPL v2 NOTICE:
echo This software integrates and redistributes components of TightVNC 2.8.90
echo under the GNU General Public License version 2 (GPL v2).
echo Upstream Copyright (C) GlavSoft LLC and original TightVNC contributors.
echo Source code archive is available in the repository root.
echo ===============================================================================
) > "%DIST_DIR%\README.txt"

copy /y "..\tightvnc-bin\LICENCE.txt" "%DIST_DIR%\GPL-LICENSE.txt" >nul

echo.
echo [1/2] Creating Lightweight ZIP Archive: %ZIP_LIGHT%...
powershell -NoProfile -Command "Remove-Item '%ZIP_LIGHT%' -Force -ErrorAction SilentlyContinue; Compress-Archive -Path '%DIST_DIR%\*' -DestinationPath '%ZIP_LIGHT%'"

echo.
echo [2/2] Assembling Standalone Package with Bundled Runtime...
if not exist "%STANDALONE_DIR%" mkdir "%STANDALONE_DIR%"
xcopy /E /I /Y "%DIST_DIR%" "%STANDALONE_DIR%" >nul
if not exist "%STANDALONE_DIR%\runtime" mkdir "%STANDALONE_DIR%\runtime" >nul
if exist "C:\Program Files\nodejs\node.exe" (
    copy /y "C:\Program Files\nodejs\node.exe" "%STANDALONE_DIR%\runtime\" >nul
    echo Bundled node.exe into runtime directory.
    echo Creating Standalone ZIP Archive: %ZIP_STANDALONE%...
    powershell -NoProfile -Command "Remove-Item '%ZIP_STANDALONE%' -Force -ErrorAction SilentlyContinue; Compress-Archive -Path '%STANDALONE_DIR%\*' -DestinationPath '%ZIP_STANDALONE%'"
)

echo.
echo =========================================================
echo [SUCCESS] MSK Remote Packages Created Successfully!
echo Lightweight (1 MB):  %ZIP_LIGHT%
echo Standalone (30 MB): %ZIP_STANDALONE%
echo App Directory:      %DIST_DIR%
echo =========================================================

endlocal
