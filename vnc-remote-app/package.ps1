# asynchronous Desktop App Packaging Script
Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host "  Packaging asynchronous Application (Release)           " -ForegroundColor Cyan
Write-Host "=========================================================" -ForegroundColor Cyan

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = Resolve-Path "$scriptDir\.."
$distDir = "$rootDir\dist\asynchronous"
$distLegacyDir = "$rootDir\dist\VNCremoteconnect"
$standaloneDir = "$rootDir\dist\asynchronous-Standalone"

$zipLight = "$rootDir\dist\asynchronous-v1.0.0-Lightweight.zip"
$zipStandalone = "$rootDir\dist\asynchronous-v1.0.0-Standalone-with-Runtime.zip"
$zipLegacyLight = "$rootDir\dist\VNCremoteconnect-v1.0.0-Lightweight.zip"
$zipLegacyStandalone = "$rootDir\dist\VNCremoteconnect-v1.0.0-Standalone-with-Runtime.zip"

# 1. Compile asynchronous.exe
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

if (-not (Test-Path "$scriptDir\bin")) {
    New-Item -ItemType Directory -Path "$scriptDir\bin" -Force | Out-Null
}

Write-Host "Compiling asynchronous.exe..." -ForegroundColor Yellow
$compileArgs = @(
    "/nologo",
    "/target:winexe",
    "/optimize+",
    "/platform:anycpu",
    "/lib:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF",
    "/r:PresentationFramework.dll",
    "/r:PresentationCore.dll",
    "/r:WindowsBase.dll",
    "/r:System.Xaml.dll",
    "/r:System.dll",
    "/r:System.Core.dll",
    "/r:System.Web.Extensions.dll",
    "/out:$scriptDir\bin\asynchronous.exe",
    "$scriptDir\src\MainWindow.cs"
)

& $csc $compileArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "Compilation failed!"
    exit 1
}
Write-Host "[SUCCESS] asynchronous.exe compiled successfully! ($((Get-Item "$scriptDir\bin\asynchronous.exe").Length) bytes)" -ForegroundColor Green

# 2. Assemble Distribution Folders
Write-Host "Assembling application distribution files..." -ForegroundColor Yellow
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path "$distDir\engine" -Force | Out-Null
New-Item -ItemType Directory -Path "$distDir\vnc" -Force | Out-Null

Copy-Item "$scriptDir\bin\asynchronous.exe" "$distDir\" -Force
Copy-Item "$rootDir\vnc-remote-core\vnc-engine.js" "$distDir\engine\" -Force
Copy-Item "$rootDir\vnc-remote-core\vnc-e2ee.js" "$distDir\engine\" -Force
Copy-Item "$rootDir\vnc-remote-core\relay.pfx" "$distDir\engine\" -Force

# Copy portable TightVNC binaries
if (Test-Path "$rootDir\tightvnc-bin\vncviewer.exe") {
    Copy-Item "$rootDir\tightvnc-bin\vncviewer.exe" "$distDir\vnc\" -Force
    Copy-Item "$rootDir\tightvnc-bin\WinVNC.exe" "$distDir\vnc\" -Force
    Copy-Item "$rootDir\tightvnc-bin\VNCHooks.dll" "$distDir\vnc\" -Force
}
if (Test-Path "C:\Program Files\TightVNC\tvnviewer.exe") {
    Copy-Item "C:\Program Files\TightVNC\tvnviewer.exe" "$distDir\vnc\" -Force
}

# Launcher scripts
@"
@echo off
cd /d "%~dp0"
start "" "asynchronous.exe"
"@ | Set-Content -Path "$distDir\Start-asynchronous.bat" -Encoding ASCII

# Root launcher
@"
@echo off
cd /d "%~dp0"
start "" "dist\asynchronous\asynchronous.exe"
"@ | Set-Content -Path "$rootDir\Launch-asynchronous.bat" -Encoding ASCII

# Readme & License
@"
===============================================================================
  asynchronous - Windows Remote Desktop Application (v1.0.0)
===============================================================================

asynchronous enables secure, lightweight remote desktop connections across
different networks, firewalls, and college Wi-Fi without manual port forwarding.

KEY FEATURES:
- Permanent Unattended Access: Permanent unique 9-digit Device ID and Password.
- Automatic Hosting: Immediately online on launch with zero sharing prompts.
- Seamless Reconnect: Reconnect anytime with the exact same ID and Password.
- Cross-Version Compatible: Connect between older and newer versions seamlessly.
- Authenticated End-to-End Encryption: X25519 ECDH + SHA-256 HKDF + AES-256-GCM.
- TightVNC Engine Integration: Hardware-accelerated, low bandwidth consumption.
- Native Windows GUI: Process name 'asynchronous', instantaneous launch.

QUICK START:
1. Run 'asynchronous.exe' or 'Start-asynchronous.bat'.
2. PERMANENT UNATTENDED ACCESS:
   - When opened, the app assigns you a permanent Unique Device ID and Password.
   - Hosting is AUTOMATICALLY ONLINE. No need to click 'Start Sharing' or confirm popups.
   - Give your Device ID and Password to your authorized viewer.
3. TO CONNECT / RECONNECT:
   - Enter partner's Device ID and Password in the Viewer tab.
   - Click 'Connect to Remote PC'.
   - If disconnected at any time, click 'Reconnect' to reconnect immediately!

TIGHTVNC GPL v2 NOTICE:
This software integrates and redistributes components of TightVNC 2.8.90
under the GNU General Public License version 2 (GPL v2).
Upstream Copyright (C) GlavSoft LLC and original TightVNC contributors.
===============================================================================
"@ | Set-Content -Path "$distDir\README.txt" -Encoding UTF8

Copy-Item "$rootDir\tightvnc-bin\LICENCE.txt" "$distDir\GPL-LICENSE.txt" -Force

# Also sync to legacy dist folder for backward compatibility
if (Test-Path $distLegacyDir) { Remove-Item $distLegacyDir -Recurse -Force -ErrorAction SilentlyContinue }
Copy-Item $distDir $distLegacyDir -Recurse -Force
Copy-Item "$scriptDir\bin\asynchronous.exe" "$distLegacyDir\VNCremoteconnect.exe" -Force

# 3. Create Lightweight ZIP
Add-Type -AssemblyName System.IO.Compression.FileSystem
Write-Host "Creating Lightweight ZIP Archive..." -ForegroundColor Yellow
if (Test-Path $zipLight) { Remove-Item $zipLight -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($distDir, $zipLight, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Copy-Item $zipLight $zipLegacyLight -Force
Write-Host "[SUCCESS] Created: $zipLight ($([math]::Round((Get-Item $zipLight).Length / 1MB, 2)) MB)" -ForegroundColor Green

# 4. Assemble Standalone Package
Write-Host "Assembling Standalone Package with Bundled Runtime..." -ForegroundColor Yellow
if (Test-Path $standaloneDir) { Remove-Item $standaloneDir -Recurse -Force -ErrorAction SilentlyContinue }
Copy-Item $distDir $standaloneDir -Recurse -Force
New-Item -ItemType Directory -Path "$standaloneDir\runtime" -Force | Out-Null
if (Test-Path "C:\Program Files\nodejs\node.exe") {
    Copy-Item "C:\Program Files\nodejs\node.exe" "$standaloneDir\runtime\" -Force
    if (Test-Path $zipStandalone) { Remove-Item $zipStandalone -Force }
    Write-Host "Creating Standalone ZIP Archive (compressing portable runtime)..." -ForegroundColor Yellow
    [System.IO.Compression.ZipFile]::CreateFromDirectory($standaloneDir, $zipStandalone, [System.IO.Compression.CompressionLevel]::Optimal, $false)
    Copy-Item $zipStandalone $zipLegacyStandalone -Force
    Write-Host "[SUCCESS] Created: $zipStandalone ($([math]::Round((Get-Item $zipStandalone).Length / 1MB, 2)) MB)" -ForegroundColor Green
}

Write-Host "`n=========================================================" -ForegroundColor Cyan
Write-Host "  asynchronous PACKAGING COMPLETE!                       " -ForegroundColor Cyan
Write-Host "  Process Name:        asynchronous.exe                  " -ForegroundColor White
Write-Host "  Lightweight Package: $zipLight" -ForegroundColor White
Write-Host "  Standalone Package:  $zipStandalone" -ForegroundColor White
Write-Host "  Extracted App:       $distDir" -ForegroundColor White
Write-Host "=========================================================" -ForegroundColor Cyan
