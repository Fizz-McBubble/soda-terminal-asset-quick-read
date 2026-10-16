[CmdletBinding()]
param([string]$OutputDirectory, [switch]$RunTests, [string]$DotNetPath = (Get-Command dotnet -ErrorAction Stop).Source, [string]$TemporaryDirectory)
$ErrorActionPreference = 'Stop'
$derivedRoot = if ($env:SODA_DERIVED_ROOT) { $env:SODA_DERIVED_ROOT } else { (Join-Path $PSScriptRoot '.cache\soda-derived') }
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $derivedRoot ('asset-quick-read\bridge\build-' + [guid]::NewGuid().ToString('N'))
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Output must be a new directory; existing artifacts are preserved.' }
$sdkDirectory = Split-Path -Parent $DotNetPath
if (-not (Test-Path -LiteralPath $DotNetPath)) { throw 'Installed .NET 8 SDK required; nothing is downloaded.' }
if (-not $TemporaryDirectory) { $TemporaryDirectory = if ($env:SODA_BUILD_TEMP_DIRECTORY) { $env:SODA_BUILD_TEMP_DIRECTORY } else { Join-Path $derivedRoot 'temp\asset-quick-read-bridge' } }
$TemporaryDirectory = [IO.Path]::GetFullPath($TemporaryDirectory)
$buildEnvironmentBefore = @{}
foreach ($name in @('TEMP', 'TMP', 'DOTNET_CLI_HOME', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_ROOT', 'NUGET_PACKAGES', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE')) { $buildEnvironmentBefore[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    New-Item -ItemType Directory -Path $TemporaryDirectory -Force | Out-Null
    $env:TEMP = $TemporaryDirectory
    $env:TMP = $TemporaryDirectory
    if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $derivedRoot 'dotnet\cli-home' }
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ROOT = $sdkDirectory
    # Use the standard SDK package cache.
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $project = Join-Path $PSScriptRoot 'bridge\Soda.AssetQuickRead.Bridge.csproj'
    $intermediate = Join-Path $output 'intermediate\'
    $companion = Join-Path $output 'companion'
    # The host fixture validates a versioned installation layout. Publish once
    # there, test the real apphost, then move those same bytes to the public exit.
    $published = if ($RunTests) { Join-Path $output 'host-fixture\versions\1.0.8' } else { $companion }
    & $DotNetPath publish $project --configuration Release --runtime win-x64 --self-contained true "-p:BaseIntermediateOutputPath=$intermediate" "-p:BaseOutputPath=$(Join-Path $output 'bin\')" "-p:RuntimeFrameworkVersion=8.0.29" --output $published
    if ($LASTEXITCODE -ne 0) { throw 'Bridge offline publish failed.' }
    if ($RunTests) {
        $fixtureExe = Join-Path $published 'Soda.AssetQuickRead.Bridge.exe'
        $testProcess = Start-Process -FilePath $fixtureExe -ArgumentList '--self-test' -Wait -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $output 'host-self-tests.json') -RedirectStandardError (Join-Path $output 'host-self-tests-error.log')
        if ($testProcess.ExitCode -ne 0) { throw 'Bridge synthetic self-tests failed.' }
        $ownedPrefix = [IO.Path]::GetFullPath($output).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        foreach ($path in @($published, $companion)) {
            $resolved = [IO.Path]::GetFullPath($path)
            if (-not $resolved.StartsWith($ownedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture move outside owned output rejected.' }
            for ($cursor = $resolved; $cursor; $cursor = Split-Path -Parent $cursor) {
                if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked fixture move rejected.' }
            }
        }
        if ((Test-Path -LiteralPath $companion) -or -not (Test-Path -LiteralPath $published -PathType Container)) { throw 'Unknown fixture move target rejected.' }
        Move-Item -LiteralPath $published -Destination $companion
    }
    $exe = Join-Path $companion 'Soda.AssetQuickRead.Bridge.exe'
    [pscustomobject]@{ outputDirectory = $output; executable = $exe; sha256 = (Get-FileHash -LiteralPath $exe).Hash; bridgeAssemblySha256 = (Get-FileHash -LiteralPath (Join-Path $output 'companion\Soda.AssetQuickRead.Bridge.dll')).Hash; captureStarted = $false; networkDownloads = $false } | ConvertTo-Json
} finally {
    foreach ($name in $buildEnvironmentBefore.Keys) {
        if ($null -eq $buildEnvironmentBefore[$name]) { [Environment]::SetEnvironmentVariable($name, [NullString]::Value, 'Process') }
        else { [Environment]::SetEnvironmentVariable($name, $buildEnvironmentBefore[$name], 'Process') }
    }
}
