[CmdletBinding()]
param([string]$CompanionDirectory = $PSScriptRoot)
$ErrorActionPreference = 'Stop'
$exe = Join-Path ([IO.Path]::GetFullPath($CompanionDirectory)) 'Soda.AssetQuickRead.Bridge.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Provide the independent packaged companion directory. Build-Bridge then Package-AssetQuickRead prepare it without starting capture.' }
& $exe --serve
exit $LASTEXITCODE
