[CmdletBinding()]
param([switch]$Offline, [string]$UpstreamDirectory, [string]$DownloadDirectory, [string]$TemporaryDirectory)
$ErrorActionPreference = 'Stop'
$packageRoot = $PSScriptRoot
$sourcePin = Get-Content -LiteralPath (Join-Path $packageRoot 'upstream-source.json') -Raw | ConvertFrom-Json
$derivedRoot = if ($env:SODA_DERIVED_ROOT) { $env:SODA_DERIVED_ROOT } else { (Join-Path $PSScriptRoot '.cache\soda-derived') }
$manifestUpstream = Join-Path $packageRoot '.upstream'
# Existing verified inputs remain usable; new inputs live in the derived root.
$upstreamDirectory = if ($UpstreamDirectory) { [IO.Path]::GetFullPath($UpstreamDirectory) } elseif (Test-Path -LiteralPath $manifestUpstream) { $manifestUpstream } else { Join-Path $derivedRoot 'asset-quick-read\upstream' }
if (-not $TemporaryDirectory) { $TemporaryDirectory = if ($env:SODA_BUILD_TEMP_DIRECTORY) { $env:SODA_BUILD_TEMP_DIRECTORY } else { Join-Path $derivedRoot 'temp\asset-quick-read-upstream' } }
$buildEnvironmentBefore = @{}
foreach ($name in @('TEMP', 'TMP')) { $buildEnvironmentBefore[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
New-Item -ItemType Directory -Path $TemporaryDirectory -Force | Out-Null
$env:TEMP = $TemporaryDirectory
$env:TMP = $TemporaryDirectory
if ($UpstreamDirectory -and (Test-Path -LiteralPath $manifestUpstream)) {
    $existing = Get-Item -LiteralPath $manifestUpstream
    $actual = if ($existing.LinkType) { $existing.ResolveLinkTarget($true).FullName } else { $existing.FullName }
    if ([IO.Path]::GetFullPath($actual) -ne [IO.Path]::GetFullPath($upstreamDirectory)) { throw 'Existing Cargo upstream differs from explicit input; no link was changed.' }
}
$sourceHashes = Get-Content -LiteralPath (Join-Path $packageRoot 'upstream-files.sha256.json') -Raw | ConvertFrom-Json
if (-not (Test-Path -LiteralPath $upstreamDirectory)) {
    $downloadDirectory = if ($DownloadDirectory) { [IO.Path]::GetFullPath($DownloadDirectory) } else { Join-Path $derivedRoot 'asset-quick-read\downloads' }
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
    New-Item -ItemType Directory -Path $upstreamDirectory -Force | Out-Null
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

# Cargo's portable path dependencies use this small link, never a second source copy.
if (-not (Test-Path -LiteralPath $manifestUpstream)) { New-Item -ItemType Junction -Path $manifestUpstream -Target $upstreamDirectory | Out-Null }
} finally {
    foreach ($name in $buildEnvironmentBefore.Keys) {
        [Environment]::SetEnvironmentVariable($name, $buildEnvironmentBefore[$name], 'Process')
    }
}
