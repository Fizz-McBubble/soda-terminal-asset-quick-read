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
foreach ($name in @('TEMP', 'TMP', 'DOTNET_CLI_HOME', 'DOTNET_GENERATE_ASPNET_CERTIFICATE')) { $buildEnvironmentBefore[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
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
    & $DotNetPath publish $project --configuration Release --runtime win-x64 --self-contained true "-p:BaseIntermediateOutputPath=$intermediate" "-p:BaseOutputPath=$(Join-Path $output 'bin\')" "-p:RuntimeFrameworkVersion=8.0.29" --output (Join-Path $output 'companion')
    if ($LASTEXITCODE -ne 0) { throw 'Bridge offline publish failed.' }
    $exe = Join-Path $output 'companion\Soda.AssetQuickRead.Bridge.exe'
    if ($RunTests) { & $exe --self-test; if ($LASTEXITCODE -ne 0) { throw 'Bridge synthetic self-tests failed.' } }
    [pscustomobject]@{ outputDirectory = $output; executable = $exe; sha256 = (Get-FileHash -LiteralPath $exe).Hash; bridgeAssemblySha256 = (Get-FileHash -LiteralPath (Join-Path $output 'companion\Soda.AssetQuickRead.Bridge.dll')).Hash; captureStarted = $false; networkDownloads = $false } | ConvertTo-Json
} finally {
    foreach ($name in $buildEnvironmentBefore.Keys) {
        if ($null -eq $buildEnvironmentBefore[$name]) { [Environment]::SetEnvironmentVariable($name, [NullString]::Value, 'Process') }
        else { [Environment]::SetEnvironmentVariable($name, $buildEnvironmentBefore[$name], 'Process') }
    }
}
