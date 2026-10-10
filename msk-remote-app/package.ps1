# VNC Remote Connect Packaging Script
Write-Host "=========================================================" -ForegroundColor Cyan
Write-Host "  Packaging VNC Remote Connect Application (Release)     " -ForegroundColor Cyan
Write-Host "=========================================================" -ForegroundColor Cyan

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = Resolve-Path "$scriptDir\.."
$distDir = "$rootDir\dist\VNCremoteconnect"
$standaloneDir = "$rootDir\dist\VNCremoteconnect-Standalone"
$zipLight = "$rootDir\dist\VNCremoteconnect-v1.0.0-Lightweight.zip"
$zipStandalone = "$rootDir\dist\VNCremoteconnect-v1.0.0-Standalone-with-Runtime.zip"

# 1. Compile VNCremoteconnect.exe
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) {
    $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

if (-not (Test-Path "$scriptDir\bin")) {
    New-Item -ItemType Directory -Path "$scriptDir\bin" -Force | Out-Null
}

Write-Host "Compiling VNCremoteconnect.exe..." -ForegroundColor Yellow
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
    "/out:$scriptDir\bin\VNCremoteconnect.exe",
    "$scriptDir\src\MainWindow.cs"
)

& $csc $compileArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "Compilation failed!"
    exit 1
}
Write-Host "[SUCCESS] VNCremoteconnect.exe compiled successfully! ($((Get-Item "$scriptDir\bin\VNCremoteconnect.exe").Length) bytes)" -ForegroundColor Green

# 2. Assemble VNCremoteconnect Directory
Write-Host "Assembling VNCremoteconnect files..." -ForegroundColor Yellow
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Path "$distDir\engine" -Force | Out-Null
New-Item -ItemType Directory -Path "$distDir\vnc" -Force | Out-Null

Copy-Item "$scriptDir\bin\VNCremoteconnect.exe" "$distDir\" -Force
Copy-Item "$rootDir\msk-remote-poc\msk-engine.js" "$distDir\engine\" -Force
Copy-Item "$rootDir\msk-remote-poc\msk-e2ee.js" "$distDir\engine\" -Force
Copy-Item "$rootDir\msk-remote-poc\relay.pfx" "$distDir\engine\" -Force

# Copy portable TightVNC binaries
if (Test-Path "$rootDir\tightvnc-bin\vncviewer.exe") {
    Copy-Item "$rootDir\tightvnc-bin\vncviewer.exe" "$distDir\vnc\" -Force
    Copy-Item "$rootDir\tightvnc-bin\WinVNC.exe" "$distDir\vnc\" -Force
    Copy-Item "$rootDir\tightvnc-bin\VNCHooks.dll" "$distDir\vnc\" -Force
}
if (Test-Path "C:\Program Files\TightVNC\tvnviewer.exe") {
    Copy-Item "C:\Program Files\TightVNC\tvnviewer.exe" "$distDir\vnc\" -Force
}

# Launcher
@"
@echo off
cd /d "%~dp0"
start "" "VNCremoteconnect.exe"
"@ | Set-Content -Path "$distDir\Start-VNCremoteconnect.bat" -Encoding ASCII

# Readme & License
@"
===============================================================================
  VNC REMOTE CONNECT - Windows Remote Desktop Application (v1.0.0)
===============================================================================

VNC Remote Connect enables secure, lightweight remote desktop connections across
different networks, firewalls, and college Wi-Fi without manual port forwarding.

KEY FEATURES:
- Zero-Knowledge Relay Architecture: Relay transfers opaque ciphertext frames.
- Authenticated End-to-End Encryption: X25519 ECDH + SHA-256 HKDF + AES-256-GCM.
- TightVNC Engine Integration: Maximum performance, low bandwidth consumption.
- Native Windows GUI: No Electron bloat, instantaneous launch.

QUICK START:
1. Run 'VNCremoteconnect.exe' or 'Start-VNCremoteconnect.bat'.
2. TO SHARE SCREEN:
   - Click 'Start Sharing'.
   - Give your 9-digit Device ID and Session PIN to your partner.
3. TO CONNECT:
   - Enter partner's Device ID and Session PIN.
   - Click 'Connect to Remote PC'.

TIGHTVNC GPL v2 NOTICE:
This software integrates and redistributes components of TightVNC 2.8.90
under the GNU General Public License version 2 (GPL v2).
Upstream Copyright (C) GlavSoft LLC and original TightVNC contributors.
Source code archive is available in the repository root.
===============================================================================
"@ | Set-Content -Path "$distDir\README.txt" -Encoding UTF8

Copy-Item "$rootDir\tightvnc-bin\LICENCE.txt" "$distDir\GPL-LICENSE.txt" -Force

# 3. Create Lightweight ZIP
Add-Type -AssemblyName System.IO.Compression.FileSystem
Write-Host "Creating Lightweight ZIP Archive..." -ForegroundColor Yellow
if (Test-Path $zipLight) { Remove-Item $zipLight -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($distDir, $zipLight, [System.IO.Compression.CompressionLevel]::Optimal, $false)
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
    Write-Host "[SUCCESS] Created: $zipStandalone ($([math]::Round((Get-Item $zipStandalone).Length / 1MB, 2)) MB)" -ForegroundColor Green
}

Write-Host "`n=========================================================" -ForegroundColor Cyan
Write-Host "  VNC REMOTE CONNECT PACKAGING COMPLETE!                 " -ForegroundColor Cyan
Write-Host "  Lightweight Package: $zipLight" -ForegroundColor White
Write-Host "  Standalone Package:  $zipStandalone" -ForegroundColor White
Write-Host "  Extracted App:       $distDir" -ForegroundColor White
Write-Host "=========================================================" -ForegroundColor Cyan
