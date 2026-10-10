[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$credOut = @"
protocol=https
host=github.com

"@ | git credential fill

$token = ($credOut | Select-String 'password=(.*)').Matches.Groups[1].Value
$headers = @{
    "Authorization" = "Bearer $token"
    "User-Agent" = "powershell"
    "Accept" = "application/vnd.github.v3+json"
}

$releaseId = 408848208
$repo = "vihivincorp/VNCremoteconnect"

# 1. Update Release Title and Body
$updatePayload = @{
    name = "asynchronous v1.0.0 (VNC Remote Connect)"
    body = @"
# asynchronous v1.0.0 - Windows Remote Desktop

High-speed, end-to-end encrypted remote desktop control for Windows with permanent unattended credentials and automatic cross-version updates.

### Download Packages:
- **asynchronous-v1.0.0-Lightweight.zip** (0.96 MB) - Instant < 1s download. Contains `asynchronous.exe`, E2EE engine, and TightVNC binaries.
- **asynchronous-v1.0.0-Standalone-with-Runtime.zip** (32 MB) - Includes bundled portable runtime.

### Highlights:
- **Permanent Credentials**: Permanent Unique 9-digit Device ID and Password saved on device.
- **Zero-Click Unattended Access**: Automatically online on launch. No 'Start Sharing' or confirmation popups.
- **Seamless Reconnect**: Reconnect anytime using the same ID and Password.
- **Process Name**: Runs as `asynchronous.exe` / `asynchronous`.
- **Automatic Update Checking**: Automatically notifies when a new version is released on GitHub.
"@
} | ConvertTo-Json

Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/$releaseId" -Method Patch -Headers $headers -Body $updatePayload -ContentType "application/json" | Out-Null
Write-Host "[SUCCESS] Release metadata updated to 'asynchronous v1.0.0'!"

# 2. Get existing assets and delete old versions
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/$releaseId" -Headers $headers
$existingAssets = $release.assets

function Upload-Asset($filePath, $assetName) {
    # Delete if exists
    $found = $existingAssets | Where-Object { $_.name -eq $assetName }
    if ($found) {
        Write-Host "Deleting old asset: $assetName (ID: $($found.id))..."
        Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/assets/$($found.id)" -Method Delete -Headers $headers | Out-Null
        Start-Sleep -Seconds 1
    }

    Write-Host "Uploading $assetName ($([math]::Round((Get-Item $filePath).Length / 1MB, 2)) MB)..."
    $uploadHeaders = @{
        "Authorization" = "Bearer $token"
        "User-Agent" = "powershell"
        "Content-Type" = "application/zip"
    }

    $uploadUrl = "https://uploads.github.com/repos/$repo/releases/$releaseId/assets?name=$assetName"
    $bytes = [System.IO.File]::ReadAllBytes($filePath)

    $req = [System.Net.HttpWebRequest]::Create($uploadUrl)
    $req.Method = "POST"
    $req.Headers["Authorization"] = "Bearer $token"
    $req.UserAgent = "powershell"
    $req.ContentType = "application/zip"
    $req.ContentLength = $bytes.Length
    $req.Timeout = 180000

    $reqStream = $req.GetRequestStream()
    $reqStream.Write($bytes, 0, $bytes.Length)
    $reqStream.Close()

    $resp = $req.GetResponse()
    $reader = New-Object System.IO.StreamReader($resp.GetResponseStream())
    $respText = $reader.ReadToEnd()
    $reader.Close()
    $resp.Close()

    Write-Host "[SUCCESS] Uploaded $assetName!"
}

$rootDir = Resolve-Path "$PSScriptRoot\.."

# Upload both new 'asynchronous' zips and legacy zips so every link works
Upload-Asset "$rootDir\dist\asynchronous-v1.0.0-Lightweight.zip" "asynchronous-v1.0.0-Lightweight.zip"
Upload-Asset "$rootDir\dist\VNCremoteconnect-v1.0.0-Lightweight.zip" "VNCremoteconnect-v1.0.0-Lightweight.zip"

Write-Host "Release assets upload complete!"
