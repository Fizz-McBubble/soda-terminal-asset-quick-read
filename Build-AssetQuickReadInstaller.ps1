[CmdletBinding()]
param([Parameter(Mandatory)][string]$BridgeOutputDirectory, [Parameter(Mandatory)][string]$OutputDirectory, [string]$NsisPath, [string]$DotNetPath=(Get-Command dotnet -ErrorAction Stop).Source, [switch]$RunTests, [string]$TemporaryDirectory, [string]$RuntimeDirectory, [string]$ReusePublishedPayloadDirectory)
$ErrorActionPreference='Stop'
$derivedRoot = if ($env:SODA_DERIVED_ROOT) { $env:SODA_DERIVED_ROOT } else { (Join-Path $PSScriptRoot '.cache\soda-derived') }
if (-not $TemporaryDirectory) { $TemporaryDirectory = if ($env:SODA_BUILD_TEMP_DIRECTORY) { $env:SODA_BUILD_TEMP_DIRECTORY } else { Join-Path $derivedRoot 'temp\asset-quick-read-installer' } }
$buildEnvironmentBefore = @{}
foreach ($name in @('TEMP', 'TMP', 'DOTNET_CLI_HOME', 'DOTNET_ROOT', 'NUGET_PACKAGES', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE')) { $buildEnvironmentBefore[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
New-Item -ItemType Directory -Path $TemporaryDirectory -Force | Out-Null
$env:TEMP = $TemporaryDirectory
$env:TMP = $TemporaryDirectory
if (-not $env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $derivedRoot 'dotnet\cli-home' }
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Output must be new; existing evidence is never overwritten.'}
$pin=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installer/nsis-toolchain.json') -Raw | ConvertFrom-Json
if($NsisPath -and (Get-FileHash -LiteralPath $NsisPath).Hash.ToLowerInvariant() -cne $pin.compilerSha256){throw 'Pinned NSIS compiler mismatch.'}
$sdk=Split-Path -Parent $DotNetPath
$env:DOTNET_ROOT=$sdk; # Standard SDK package cache.
 $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'
New-Item -ItemType Directory -Path $output | Out-Null
if ($ReusePublishedPayloadDirectory) {
    # Reuse immutable reviewed payload bytes; only the installer changes.
    $oldCatalogPath=Join-Path $ReusePublishedPayloadDirectory 'catalog.json'
    $publishedCatalog=Join-Path $PSScriptRoot 'installer/published-1.0.3-catalog.json'
    if((Get-FileHash -LiteralPath $oldCatalogPath).Hash -cne (Get-FileHash -LiteralPath $publishedCatalog).Hash){throw 'Published payload catalog mismatch.'}
    $oldCatalog=Get-Content -LiteralPath $oldCatalogPath -Raw | ConvertFrom-Json
    $archive=Join-Path $ReusePublishedPayloadDirectory 'setup/payload/asset-quick-read.zip'
    if((Get-FileHash -LiteralPath $archive).Hash -cne $oldCatalog.archiveSha256){throw 'Published payload archive mismatch.'}
    $catalogFiles=$oldCatalog.files
    # NSIS reads the existing immutable archive directly; no runtime copy is made.
    if(-not $NsisPath){throw 'Published payload reuse requires the pinned NSIS wrapper.'}
    $payloadDirectory=Join-Path $output 'setup/payload'; [IO.Directory]::CreateDirectory($payloadDirectory) | Out-Null
} else {
$privatePackage=Join-Path $output 'private-complete-package'
& (Join-Path $PSScriptRoot 'Package-AssetQuickRead.ps1') -BridgeOutputDirectory $BridgeOutputDirectory -DestinationDirectory $privatePackage -RuntimeDirectory $RuntimeDirectory
$payloadRoot=Join-Path $output 'public-embedded-payload'; [IO.Directory]::CreateDirectory($payloadRoot) | Out-Null
$remote=@('runtime/assets/manifest.json','runtime/assets/datamine.json','runtime/assets/nap.json')
foreach($file in Get-ChildItem -LiteralPath $privatePackage -File -Recurse){
    $relative=[IO.Path]::GetRelativePath($privatePackage,$file.FullName).Replace('\','/')
    if($relative -cin $remote){continue}
    $target=Join-Path $payloadRoot $relative; [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null; [IO.File]::Copy($file.FullName,$target,$false)
}
$licenses=Join-Path $payloadRoot 'licenses'; [IO.Directory]::CreateDirectory($licenses) | Out-Null
$rustNotice=Join-Path $PSScriptRoot 'installer/Rust-DEPENDENCY-NOTICES.txt'
$lockHash=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Cargo.lock')).Hash
if(-not(Test-Path -LiteralPath $rustNotice) -or -not ([IO.File]::ReadAllText($rustNotice).Contains("Cargo.lock SHA256: $lockHash"))){throw 'Complete locked Rust notices missing or stale; run Build-RustDependencyNotices.ps1 offline first.'}
foreach($name in @('Rust-DEPENDENCY-NOTICES.txt','Rust-STDLIB-COPYRIGHT.html')){
    $noticeSource=Join-Path $PSScriptRoot ('installer/'+$name)
    if(-not(Test-Path -LiteralPath $noticeSource)){throw "Required complete notice missing: $name"}
    [IO.File]::Copy($noticeSource,(Join-Path $licenses $name),$false)
}
if($NsisPath){[IO.File]::Copy((Join-Path $PSScriptRoot 'installer/NSIS-COPYING.txt'),(Join-Path $licenses 'NSIS-COPYING.txt'),$false)}
foreach($package in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64','microsoft.aspnetcore.app.runtime.win-x64')){
    $names=if($package -eq 'microsoft.netcore.app.runtime.win-x64'){@('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')}elseif($package -eq 'microsoft.windowsdesktop.app.runtime.win-x64'){@('LICENSE')}else{@('THIRD-PARTY-NOTICES.TXT')}
    foreach($name in $names){
        $source=Join-Path $env:NUGET_PACKAGES "$package/8.0.29/$name"
        if(-not(Test-Path -LiteralPath $source)){throw "Required runtime license missing: $package/$name"}
        [IO.File]::Copy($source,(Join-Path $licenses "$package-$name"),$false)
    }
    # Some SDK runtime packs store additional notices only inside the original nupkg.
    # Include every such notice rather than assuming the extracted cache is complete.
    $nupkg=Join-Path $env:NUGET_PACKAGES "$package/8.0.29/$package.8.0.29.nupkg"
    $runtimeZip=[IO.Compression.ZipFile]::OpenRead($nupkg)
    try{foreach($entry in $runtimeZip.Entries | Where-Object FullName -match '(?i)(^|/)(THIRD[._-]PARTY[._-]NOTICES|NOTICE)([._-].*)?$'){
        $noticeTarget=Join-Path $licenses ($package+'-'+$entry.FullName.Replace('/','_'))
        if(-not(Test-Path -LiteralPath $noticeTarget)){
            $inputStream=$entry.Open(); $outputStream=[IO.File]::Open($noticeTarget,[IO.FileMode]::CreateNew)
            try{$inputStream.CopyTo($outputStream)}finally{$outputStream.Dispose();$inputStream.Dispose()}
        }
    }}finally{$runtimeZip.Dispose()}
}
Add-Type -AssemblyName System.IO.Compression
$payloadDirectory=Join-Path $output 'setup/payload'; [IO.Directory]::CreateDirectory($payloadDirectory) | Out-Null
$archive=Join-Path $payloadDirectory 'asset-quick-read.zip'
$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
try{foreach($file in Get-ChildItem -LiteralPath $payloadRoot -File -Recurse){
    $relative=[IO.Path]::GetRelativePath($payloadRoot,$file.FullName).Replace('\','/')
    [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.FullName,$relative,[IO.Compression.CompressionLevel]::NoCompression) | Out-Null
}}finally{$zip.Dispose()}
$catalogFiles=@(foreach($file in Get-ChildItem -LiteralPath $payloadRoot -File -Recurse){[ordered]@{path=[IO.Path]::GetRelativePath($payloadRoot,$file.FullName).Replace('\','/');size=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName).Hash;download=$false}})
$catalogFiles+=@(foreach($relative in $remote){$file=Get-Item -LiteralPath (Join-Path $privatePackage $relative); [ordered]@{path=$relative;size=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName).Hash;download=$true}})
}
$projectXml=[xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installer/Soda.AssetQuickRead.Setup.csproj') -Raw)
$version=[string]$projectXml.Project.PropertyGroup.Version
$catalog=[ordered]@{version=$version;archiveSha256=(Get-FileHash -LiteralPath $archive).Hash;files=$catalogFiles}
$catalogPath=Join-Path $output 'catalog.json'; [IO.File]::WriteAllText($catalogPath,($catalog|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
$embedded=if($NsisPath){''}else{$archive}
& $DotNetPath publish (Join-Path $PSScriptRoot 'installer/Soda.AssetQuickRead.Setup.csproj') -c Release -r win-x64 --self-contained true "-p:PayloadCatalog=$catalogPath" '-p:EmbeddedPayload=' '-p:UninstallStub=' "-p:BaseIntermediateOutputPath=$(Join-Path $output 'uninstall-intermediate/')" "-p:BaseOutputPath=$(Join-Path $output 'uninstall-bin/')" '-p:RuntimeFrameworkVersion=8.0.29' --output (Join-Path $output 'uninstall')
if($LASTEXITCODE -ne 0){throw 'Uninstaller offline publish failed.'}
$uninstallStub=Join-Path $output 'uninstall/Soda.AssetQuickRead.Setup.exe'
$stubResource=if($NsisPath){''}else{$uninstallStub}
$uninstallMetadata=Join-Path $output 'uninstall-payload.json'
[IO.File]::WriteAllText($uninstallMetadata,([ordered]@{size=(Get-Item -LiteralPath $uninstallStub).Length;sha256=(Get-FileHash -LiteralPath $uninstallStub).Hash}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
& $DotNetPath publish (Join-Path $PSScriptRoot 'installer/Soda.AssetQuickRead.Setup.csproj') -c Release -r win-x64 --self-contained true --configfile (Join-Path $PSScriptRoot 'bridge/NuGet.Config') "-p:PayloadCatalog=$catalogPath" "-p:EmbeddedPayload=$embedded" "-p:UninstallStub=$stubResource" "-p:UninstallMetadata=$uninstallMetadata" "-p:BaseIntermediateOutputPath=$(Join-Path $output 'intermediate/')" "-p:BaseOutputPath=$(Join-Path $output 'bin/')" '-p:RuntimeFrameworkVersion=8.0.29' --output (Join-Path $output 'setup')
if($LASTEXITCODE -ne 0){throw 'Installer offline publish failed.'}
$inner=Join-Path $output 'setup/Soda.AssetQuickRead.Setup.exe'; $final=Join-Path $output "SodaTerminal-AssetReader-Setup-$version.exe"
if($RunTests){
    if($NsisPath){[IO.File]::Copy($uninstallStub,(Join-Path $payloadDirectory 'uninstaller.exe'),$false); [IO.File]::WriteAllText((Join-Path $payloadDirectory 'ready'),'SODA-ASSET-QUICK-READ-PAYLOAD-1')}
    $reportPath=Join-Path $output 'installer-tests.json'
    $testProcess=Start-Process -FilePath $inner -ArgumentList @('--self-test', ('"'+$reportPath+'"')) -PassThru -Wait -WindowStyle Hidden
    if($testProcess.ExitCode -ne 0){throw 'Installer synthetic tests failed.'}
}
if($NsisPath){
    & $NsisPath /NOCONFIG /INPUTCHARSET UTF8 /V3 "/DINNER_EXE=$inner" "/DOUTPUT_EXE=$final" "/DRUNTIME_ARCHIVE=$archive" "/DUNINSTALLER_EXE=$uninstallStub" "/DINSTALLER_VERSION=$version" (Join-Path $PSScriptRoot 'installer/compress-setup.nsi')
    if($LASTEXITCODE -ne 0){throw 'Compressed installer build failed.'}
}else{[IO.File]::Copy($inner,$final,$false)}
[ordered]@{path=$final;size=(Get-Item -LiteralPath $final).Length;sha256=(Get-FileHash -LiteralPath $final).Hash;embeddedProtocolAssets=$false;bridgeVersion='1.0.3';installProtocolDownloads=3;maxParallelProtocolDownloads=3;protocolSourceAttempts=2;protocolSourceTimeoutSeconds=20;reuseVerifiedLocalProtocol=$true;captureStarted=$false;registryWritesDuringBuild=0} | ConvertTo-Json

} finally {
    foreach ($name in $buildEnvironmentBefore.Keys) {
        [Environment]::SetEnvironmentVariable($name, $buildEnvironmentBefore[$name], 'Process')
    }
}
