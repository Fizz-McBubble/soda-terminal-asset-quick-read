[CmdletBinding()]
param(
    [switch]$RunTests,
    [switch]$Offline,
    [string]$RustProfile = '',
    [string]$TargetDirectory,
    [string]$TemporaryDirectory
)
$ErrorActionPreference = 'Stop'
$derivedRoot = if ($env:SODA_DERIVED_ROOT) { $env:SODA_DERIVED_ROOT } else { (Join-Path $PSScriptRoot '.cache\soda-derived') }
$selectedTarget = if ($TargetDirectory) { $TargetDirectory }
    elseif ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR }
    else {
        Join-Path $derivedRoot 'asset-quick-read\rust-target'
    }
$selectedTarget = [IO.Path]::GetFullPath($selectedTarget)
if (-not $TemporaryDirectory) { $TemporaryDirectory = if ($env:SODA_BUILD_TEMP_DIRECTORY) { $env:SODA_BUILD_TEMP_DIRECTORY } else { Join-Path $derivedRoot 'temp\asset-quick-read-probe' } }
$TemporaryDirectory = [IO.Path]::GetFullPath($TemporaryDirectory)
$buildEnvironmentBefore = @{}
foreach ($name in @('TEMP', 'TMP')) { $buildEnvironmentBefore[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    New-Item -ItemType Directory -Path $TemporaryDirectory -Force | Out-Null
    $env:TEMP = $TemporaryDirectory
    $env:TMP = $TemporaryDirectory
    & (Join-Path $PSScriptRoot 'Prepare-Upstream.ps1') -Offline:$Offline
    if ($RustProfile) { . $RustProfile }
    # Reapply scratch after loading a toolchain profile that may set environment defaults.
    $env:TEMP = $TemporaryDirectory
    $env:TMP = $TemporaryDirectory
    # The toolchain profile must not replace the caller's selected artifact location.
    $env:CARGO_TARGET_DIR = $selectedTarget
    $cargoExecutable = (Get-Command cargo -ErrorAction Stop).Source
    $cargoMode = @()
    if ($Offline) { $cargoMode = @('--offline') }
    if ($RunTests) {
        & $cargoExecutable test --locked @cargoMode --manifest-path (Join-Path $PSScriptRoot 'Cargo.toml')
        if ($LASTEXITCODE -ne 0) { throw 'Probe tests failed; no runtime was packaged.' }
    }
    & $cargoExecutable build --release --locked @cargoMode --manifest-path (Join-Path $PSScriptRoot 'Cargo.toml')
    if ($LASTEXITCODE -ne 0) { throw 'Probe build failed; no runtime was packaged.' }
    $runtimeDirectory = Join-Path $PSScriptRoot 'runtime'
    New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $env:CARGO_TARGET_DIR 'release\soda-asset-capture.exe') -Destination $runtimeDirectory
    $protocolDirectory = Join-Path $runtimeDirectory 'assets'
    New-Item -ItemType Directory -Path $protocolDirectory -Force | Out-Null
    foreach ($file in @('manifest.json', 'datamine.json', 'nap.json')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('.upstream\assets\' + $file)) -Destination $protocolDirectory
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '.upstream\LICENSE') -Destination (Join-Path $runtimeDirectory 'UPSTREAM-LICENSE.txt')
    $driverPin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'driver-source.json') -Raw | ConvertFrom-Json
    $driverHashes = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'driver-files.sha256.json') -Raw | ConvertFrom-Json
    $downloadDirectory = Join-Path $PSScriptRoot 'downloads'
    New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
    $archive = Join-Path $downloadDirectory 'WinDivert-2.2.2-A.zip'
    if (-not (Test-Path -LiteralPath $archive)) {
        if ($Offline) { throw 'Pinned driver archive unavailable offline; no request was made.' }
        Invoke-WebRequest -Uri $driverPin.archiveUrl -OutFile $archive
    }
    if ((Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() -ne $driverPin.archiveSha256) {
        throw 'Driver archive hash mismatch; no driver was copied.'
    }
    $expandedDirectory = Join-Path $downloadDirectory 'windivert-2.2.2-official'
    if (-not (Test-Path -LiteralPath $expandedDirectory)) {
        Expand-Archive -LiteralPath $archive -DestinationPath $expandedDirectory
    }
    foreach ($file in @('WinDivert.dll', 'WinDivert64.sys')) {
        $source = Join-Path $expandedDirectory ('WinDivert-2.2.2-A\x64\' + $file)
        if ((Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant() -ne $driverHashes.$file) {
            throw ('Driver file hash mismatch: ' + $file)
        }
        if ($file -eq 'WinDivert64.sys' -and (Get-AuthenticodeSignature -LiteralPath $source).Status -ne 'Valid') {
            throw 'Driver signature is not valid; runtime was not prepared.'
        }
        $destination = Join-Path $runtimeDirectory $file
        # A loaded driver can be file-locked. Preserve an identical pinned file;
        # do not stop a shared driver merely to copy the same bytes again.
        if (-not (Test-Path -LiteralPath $destination) -or
            (Get-FileHash -LiteralPath $destination).Hash.ToLowerInvariant() -ne $driverHashes.$file) {
            Copy-Item -LiteralPath $source -Destination $destination
        }
    }
    foreach ($file in @('LICENSE', 'README', 'VERSION')) {
        $source = Join-Path $expandedDirectory ('WinDivert-2.2.2-A\' + $file)
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $runtimeDirectory ('WINDIVERT-' + $file + '.txt')) }
    }
    $executable = Join-Path $runtimeDirectory 'soda-asset-capture.exe'
    & $executable inspect
    if ($LASTEXITCODE -ne 0) { throw 'Packaged protocol inspection failed.' }
    [pscustomobject]@{ executable = $executable; sha256 = (Get-FileHash -LiteralPath $executable).Hash; captureStarted = $false } | ConvertTo-Json
} finally {
    foreach ($name in $buildEnvironmentBefore.Keys) {
        if ($null -eq $buildEnvironmentBefore[$name]) { [Environment]::SetEnvironmentVariable($name, [NullString]::Value, 'Process') }
        else { [Environment]::SetEnvironmentVariable($name, $buildEnvironmentBefore[$name], 'Process') }
    }
}
