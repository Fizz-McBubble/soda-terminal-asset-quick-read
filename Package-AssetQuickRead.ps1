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
$derivedRoot = if ($env:SODA_DERIVED_ROOT) { $env:SODA_DERIVED_ROOT } else { (Join-Path $PSScriptRoot '.cache\soda-derived') }
$runtime = if ($RuntimeDirectory) { [IO.Path]::GetFullPath($RuntimeDirectory) } elseif ($env:SODA_ASSET_RUNTIME_DIRECTORY) { $env:SODA_ASSET_RUNTIME_DIRECTORY } else { Join-Path $derivedRoot 'asset-quick-read\runtime' }
$securitySource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'bridge/Security.cs'))
$runtimePin = [regex]::Match($securitySource, '\["soda-asset-capture\.exe"\]\s*=\s*"([A-F0-9]{64})"')
if (-not $runtimePin.Success) { throw 'Reviewed Bridge runtime pin required.' }
$pins = [ordered]@{
    'soda-asset-capture.exe'=$runtimePin.Groups[1].Value
    'WinDivert.dll'='C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2'
    'WinDivert64.sys'='8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2'
    'assets\manifest.json'='38006E92317CF8EBA3E578D58E5B0FC9AECFEC117D20DC375AA9BED2B073302E'
    'assets\datamine.json'='9702AB5EE0CB44A18D8D3BE9AE2C5934DEB4883989ADA292A3BBD44654EFB449'
    'assets\nap.json'='2DFB176209CECEAB5AB53701F12FA3050AD70140004118B7A80AC32A01DF982D'
}
$driver = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'driver-files.sha256.json') -Raw | ConvertFrom-Json
$protocol = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'protocol-files.sha256.json') -Raw | ConvertFrom-Json
foreach ($p in $driver.PSObject.Properties) { if ($pins[$p.Name] -cne $p.Value.ToUpperInvariant()) { throw 'Driver manifest differs from the approved fixed identity.' } }
foreach ($p in $protocol.PSObject.Properties) { if ($pins['assets\' + $p.Name] -cne $p.Value.ToUpperInvariant()) { throw 'Protocol manifest differs from the approved fixed identity.' } }
foreach ($relative in $pins.Keys) {
    $file = Join-Path $runtime $relative; Assert-NoLinks $file
    if ((Get-FileHash -LiteralPath $file).Hash -cne $pins[$relative]) { throw 'Fixed runtime, driver, or protocol identity mismatch.' }
}
$companion = Join-Path $source 'companion'
if (-not (Test-Path -LiteralPath (Join-Path $companion 'Soda.AssetQuickRead.Bridge.exe'))) { throw 'Build-Bridge output required.' }
New-Item -ItemType Directory -Path $destination | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $companion -File) {
    if ($file.Extension -eq '.pdb') { continue }
    Assert-NoLinks $file.FullName
    [IO.File]::Copy($file.FullName, (Join-Path $destination $file.Name), $false)
}
foreach ($relative in @($pins.Keys) + @('UPSTREAM-LICENSE.txt','WINDIVERT-LICENSE.txt','WINDIVERT-README.txt','WINDIVERT-VERSION.txt')) {
    $file = Join-Path $runtime $relative; Assert-NoLinks $file
    $target = Join-Path $destination ('runtime\' + $relative)
    [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
    [IO.File]::Copy($file, $target, $false)
}
[IO.File]::Copy((Join-Path $PSScriptRoot 'Launch-AssetQuickRead.ps1'), (Join-Path $destination 'Launch-AssetQuickRead.ps1'), $false)
[pscustomobject]@{ destinationDirectory=$destination; runtimeSha256=$pins['soda-asset-capture.exe']; independentScannerPackage=$true; captureStarted=$false } | ConvertTo-Json
