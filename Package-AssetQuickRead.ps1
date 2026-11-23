[CmdletBinding()]
param([Parameter(Mandatory)][string]$BridgeOutputDirectory, [Parameter(Mandatory)][string]$DestinationDirectory, [string]$RuntimeDirectory)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($BridgeOutputDirectory)
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
if (Test-Path -LiteralPath $destination) { throw 'Destination must be new; old candidates are never overwritten.' }
function Assert-NoLinks([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked source or destination rejected.' }
        $current = Split-Path -Parent $current
    }
}
Assert-NoLinks $source
Assert-NoLinks $destination
$derivedRoot = if ($env:SODA_DERIVED_ROOT) { $env:SODA_DERIVED_ROOT } else { 'F:\AI项目\SodaTerminal\ZZZ-derived' }
$runtime = if ($RuntimeDirectory) { [IO.Path]::GetFullPath($RuntimeDirectory) } elseif ($env:SODA_ASSET_RUNTIME_DIRECTORY) { $env:SODA_ASSET_RUNTIME_DIRECTORY } else { Join-Path $derivedRoot 'asset-quick-read\runtime' }
$securitySource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'bridge/Security.cs'))
$runtimePin = [regex]::Match($securitySource, '\["soda-asset-capture\.exe"\]\s*=\s*"([A-F0-9]{64})"')
if (-not $runtimePin.Success) { throw 'Reviewed Bridge runtime pin required.' }
$pins = [ordered]@{
    'soda-asset-capture.exe'=$runtimePin.Groups[1].Value
    'WinDivert.dll'='C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2'
    'WinDivert64.sys'='8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2'
    'assets\manifest.json'='CAEBBE1A8AC8324109521B1477F67F6B10E387FC2B550486DE044027121D1CF8'
    'assets\datamine.json'='C7FAC8E4CDF0E59CAD967AA0C8A21D5DB54E746902E484DF632E65800F127F04'
    'assets\nap.json'='8FFA529920FE01265A8538BE06B821DC9B62247419C2200C837F24F4441F6DF6'
}
$driver = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'driver-files.sha256.json') -Raw | ConvertFrom-Json
$protocol = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'protocol-files.sha256.json') -Raw | ConvertFrom-Json
foreach ($p in $driver.PSObject.Properties) { if ($pins[$p.Name] -cne $p.Value.ToUpperInvariant()) { throw 'Driver manifest differs from the approved fixed identity.' } }
foreach ($p in $protocol.PSObject.Properties) { if ($pins['assets\' + $p.Name] -cne $p.Value.ToUpperInvariant()) { throw 'Protocol manifest differs from the approved fixed identity.' } }
$companion = Join-Path $source 'companion'
$bridgeExe = Join-Path $companion 'Soda.AssetQuickRead.Bridge.exe'
# Check the reviewed capture first; the compiled Bridge must then attest to
# these same pins before any driver is accessed or package directory created.
$capture = Join-Path $runtime 'soda-asset-capture.exe'
Assert-NoLinks $capture
if ((Get-FileHash -LiteralPath $capture).Hash -cne $pins['soda-asset-capture.exe']) { throw 'Fixed runtime, driver, or protocol identity mismatch.' }
if (-not (Test-Path -LiteralPath $bridgeExe)) { throw 'Build-Bridge output required.' }
Assert-NoLinks $bridgeExe
$start = [Diagnostics.ProcessStartInfo]::new($bridgeExe)
$start.ArgumentList.Add('--runtime-identity')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$probe = [Diagnostics.Process]::Start($start)
try {
    $stdout = $probe.StandardOutput.ReadToEndAsync()
    $stderr = $probe.StandardError.ReadToEndAsync()
    if (-not $probe.WaitForExit(10000)) { $probe.Kill(); throw 'Compiled Bridge identity probe timed out.' }
    if ($probe.ExitCode -ne 0) { throw 'Compiled Bridge runtime identity unavailable; rebuild the Bridge from reviewed source.' }
    try { $identity = $stdout.GetAwaiter().GetResult() | ConvertFrom-Json } catch { throw 'Invalid compiled Bridge runtime identity.' }
    if ($identity.schemaVersion -ne 1 -or $identity.captureStarted -cne $false -or $identity.serviceStarted -cne $false -or @($identity.runtime.PSObject.Properties).Count -ne $pins.Count) { throw 'Invalid compiled Bridge runtime identity.' }
    [xml]$bridgeProject = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'bridge/Soda.AssetQuickRead.Bridge.csproj') -Raw
    if ($identity.bridgeVersion -cne [string]$bridgeProject.Project.PropertyGroup.Version) { throw 'Compiled Bridge version differs from reviewed source.' }
    foreach ($relative in $pins.Keys) {
        $key = $relative.Replace('\', '/')
        if ($identity.runtime.$key -cne $pins[$relative]) { throw 'Compiled Bridge runtime pins differ from reviewed source; rebuild the Bridge.' }
    }
} finally { $probe.Dispose() }
foreach ($relative in $pins.Keys | Where-Object { $_ -ne 'soda-asset-capture.exe' }) {
    $file = Join-Path $runtime $relative; Assert-NoLinks $file
    if ((Get-FileHash -LiteralPath $file).Hash -cne $pins[$relative]) { throw 'Fixed runtime, driver, or protocol identity mismatch.' }
}
New-Item -ItemType Directory -Path $destination | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $companion -File) {
    if ($file.Extension -eq '.pdb') { continue }
    Assert-NoLinks $file.FullName
    [IO.File]::Copy($file.FullName, (Join-Path $destination $file.Name), $false)
}
foreach ($relative in @($pins.Keys) + @('UPSTREAM-LICENSE.txt','PROTOCOL-SOURCE-LICENSE.txt','WINDIVERT-LICENSE.txt','WINDIVERT-README.txt','WINDIVERT-VERSION.txt')) {
    $file = Join-Path $runtime $relative; Assert-NoLinks $file
    $target = Join-Path $destination ('runtime\' + $relative)
    [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
    [IO.File]::Copy($file, $target, $false)
}
[IO.File]::Copy((Join-Path $PSScriptRoot 'Launch-AssetQuickRead.ps1'), (Join-Path $destination 'Launch-AssetQuickRead.ps1'), $false)
[pscustomobject]@{ destinationDirectory=$destination; runtimeSha256=$pins['soda-asset-capture.exe']; independentScannerPackage=$true; captureStarted=$false } | ConvertTo-Json
