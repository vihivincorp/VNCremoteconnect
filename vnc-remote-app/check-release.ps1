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

$release = Invoke-RestMethod -Uri 'https://api.github.com/repos/vihivincorp/VNCremoteconnect/releases/408848208' -Headers $headers
Write-Host "Release ID:" $release.id "Tag:" $release.tag_name "Name:" $release.name
Write-Host "Current Assets:"
$release.assets | ForEach-Object { Write-Host " - " $_.id $_.name $_.size }
