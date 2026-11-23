[CmdletBinding()]
param([switch]$Offline, [string]$UpstreamDirectory, [string]$DownloadDirectory, [string]$TemporaryDirectory)
$ErrorActionPreference = 'Stop'
$packageRoot = $PSScriptRoot
$sourcePin = Get-Content -LiteralPath (Join-Path $packageRoot 'upstream-source.json') -Raw | ConvertFrom-Json
$protocolPin = $sourcePin.protocolSource
$protocolManifest = Get-Content -LiteralPath (Join-Path $packageRoot 'protocol-source.json') -Raw | ConvertFrom-Json
$derivedRoot = if ($env:SODA_DERIVED_ROOT) { $env:SODA_DERIVED_ROOT } else { 'F:\AI项目\SodaTerminal\ZZZ-derived' }
$manifestUpstream = Join-Path $packageRoot '.upstream'
# Existing verified inputs remain usable; new inputs live in the derived root.
if (-not $TemporaryDirectory) { $TemporaryDirectory = if ($env:SODA_BUILD_TEMP_DIRECTORY) { $env:SODA_BUILD_TEMP_DIRECTORY } else { Join-Path $derivedRoot 'temp\asset-quick-read-upstream' } }
$explicitUpstreamDirectory = $UpstreamDirectory
$selectedUpstreamDirectory = if ($explicitUpstreamDirectory) { [IO.Path]::GetFullPath($explicitUpstreamDirectory) } elseif (Test-Path -LiteralPath $manifestUpstream) { $manifestUpstream } else { Join-Path $derivedRoot 'asset-quick-read\upstream' }
$buildEnvironmentBefore = @{}
foreach ($name in @('TEMP', 'TMP')) { $buildEnvironmentBefore[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
New-Item -ItemType Directory -Path $TemporaryDirectory -Force | Out-Null
$env:TEMP = $TemporaryDirectory
$env:TMP = $TemporaryDirectory
if ($explicitUpstreamDirectory -and (Test-Path -LiteralPath $manifestUpstream)) {
    $existing = Get-Item -LiteralPath $manifestUpstream
    $actual = if ($existing.LinkType) { $existing.ResolveLinkTarget($true).FullName } else { $existing.FullName }
    if ([IO.Path]::GetFullPath($actual) -ne [IO.Path]::GetFullPath($selectedUpstreamDirectory)) { throw 'Existing Cargo upstream differs from explicit input; no link was changed.' }
}
$sourceHashes = Get-Content -LiteralPath (Join-Path $packageRoot 'upstream-files.sha256.json') -Raw | ConvertFrom-Json
if (-not (Test-Path -LiteralPath $selectedUpstreamDirectory)) {
    $downloadDirectory = if ($DownloadDirectory) { [IO.Path]::GetFullPath($DownloadDirectory) } else { Join-Path $derivedRoot 'asset-quick-read\downloads' }
    New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
    $codeArchive = Join-Path $downloadDirectory ('source-' + $sourcePin.commit + '.zip')
    $protocolArchive = Join-Path $downloadDirectory ('protocol-' + $protocolPin.commit + '.zip')
    if (-not (Test-Path -LiteralPath $codeArchive)) {
        if ($Offline) { throw 'Pinned upstream archive unavailable offline; no request was made.' }
        Invoke-WebRequest -Uri $sourcePin.archiveUrl -OutFile $codeArchive
    }
    if ((Get-FileHash -LiteralPath $codeArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sourcePin.archiveSha256) {
        throw 'Pinned parser archive hash mismatch; source was not prepared.'
    }
    if (-not (Test-Path -LiteralPath $protocolArchive)) {
        if ($Offline) { throw 'Pinned protocol archive unavailable offline; no request was made.' }
        Invoke-WebRequest -Uri $protocolPin.archiveUrl -OutFile $protocolArchive
    }
    if ((Get-FileHash -LiteralPath $protocolArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $protocolPin.archiveSha256 -or
        (Get-Item -LiteralPath $protocolArchive).Length -ne $protocolPin.archiveSize) {
        throw 'Pinned protocol archive identity mismatch; source was not prepared.'
    }
    $codeExpanded = Join-Path $downloadDirectory ('expanded-' + $sourcePin.commit)
    $protocolExpanded = Join-Path $downloadDirectory ('expanded-' + $protocolPin.commit)
    if (-not (Test-Path -LiteralPath $codeExpanded)) {
        Expand-Archive -LiteralPath $codeArchive -DestinationPath $codeExpanded
    }
    if (-not (Test-Path -LiteralPath $protocolExpanded)) {
        Expand-Archive -LiteralPath $protocolArchive -DestinationPath $protocolExpanded
    }
    $sourceDirectory = Join-Path $codeExpanded ('zzz_packet_capture_0delay-' + $sourcePin.commit)
    $protocolDirectory = Join-Path $protocolExpanded ('zzz_packet_capture-' + $protocolPin.commit)
    New-Item -ItemType Directory -Path $selectedUpstreamDirectory -Force | Out-Null
    foreach ($part in @('rust', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $sourceDirectory $part) -Destination $selectedUpstreamDirectory -Recurse
    }
    $assetDirectory = Join-Path $selectedUpstreamDirectory 'assets'
    New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
    foreach ($file in @('manifest.json', 'datamine.json', 'nap.json')) {
        Copy-Item -LiteralPath (Join-Path $protocolDirectory ('assets/' + $file)) -Destination (Join-Path $assetDirectory $file)
    }
    Copy-Item -LiteralPath (Join-Path $protocolDirectory 'LICENSE') -Destination (Join-Path $selectedUpstreamDirectory 'LICENSE.protocol')
}
foreach ($property in $sourceHashes.PSObject.Properties) {
    $file = Join-Path $selectedUpstreamDirectory $property.Name
    if (-not (Test-Path -LiteralPath $file) -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $property.Value) {
        throw ('Pinned upstream source mismatch: ' + $property.Name)
    }
}
$protocolLicense = Join-Path $selectedUpstreamDirectory 'LICENSE.protocol'
if (-not (Test-Path -LiteralPath $protocolLicense) -or
    (Get-FileHash -LiteralPath $protocolLicense -Algorithm SHA256).Hash.ToLowerInvariant() -ne $protocolManifest.licenseSha256) {
    throw 'Pinned protocol source license mismatch: LICENSE.protocol'
}
Write-Output ('Pinned parser source verified: ' + $sourcePin.commit)
Write-Output ('Pinned protocol source verified: ' + $protocolPin.commit)

# Cargo's portable path dependencies use this small link, never a second source copy.
if (-not (Test-Path -LiteralPath $manifestUpstream)) { New-Item -ItemType Junction -Path $manifestUpstream -Target $selectedUpstreamDirectory | Out-Null }
} finally {
    foreach ($name in $buildEnvironmentBefore.Keys) {
        [Environment]::SetEnvironmentVariable($name, $buildEnvironmentBefore[$name], 'Process')
    }
}
