[CmdletBinding()]
param([switch]$Offline)
$ErrorActionPreference = 'Stop'
$packageRoot = $PSScriptRoot
$sourcePin = Get-Content -LiteralPath (Join-Path $packageRoot 'upstream-source.json') -Raw | ConvertFrom-Json
$upstreamDirectory = Join-Path $packageRoot '.upstream'
$sourceHashes = Get-Content -LiteralPath (Join-Path $packageRoot 'upstream-files.sha256.json') -Raw | ConvertFrom-Json
if (-not (Test-Path -LiteralPath $upstreamDirectory)) {
    $downloadDirectory = Join-Path $packageRoot 'downloads'
    New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
    $archive = Join-Path $downloadDirectory ('source-' + $sourcePin.commit + '.zip')
    if (-not (Test-Path -LiteralPath $archive)) {
        if ($Offline) { throw 'Pinned upstream archive unavailable offline; no request was made.' }
        Invoke-WebRequest -Uri $sourcePin.archiveUrl -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sourcePin.archiveSha256) {
        throw 'Pinned upstream archive hash mismatch; source was not prepared.'
    }
    $expandedDirectory = Join-Path $downloadDirectory ('expanded-' + $sourcePin.commit)
    if (-not (Test-Path -LiteralPath $expandedDirectory)) {
        Expand-Archive -LiteralPath $archive -DestinationPath $expandedDirectory
    }
    $sourceDirectory = Join-Path $expandedDirectory ('zzz_packet_capture_0delay-' + $sourcePin.commit)
    New-Item -ItemType Directory -Path $upstreamDirectory | Out-Null
    foreach ($part in @('rust', 'assets', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $sourceDirectory $part) -Destination $upstreamDirectory -Recurse
    }
}
foreach ($property in $sourceHashes.PSObject.Properties) {
    $file = Join-Path $upstreamDirectory $property.Name
    if (-not (Test-Path -LiteralPath $file) -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $property.Value) {
        throw ('Pinned upstream source mismatch: ' + $property.Name)
    }
}
Write-Output ('Pinned upstream verified: ' + $sourcePin.commit)
