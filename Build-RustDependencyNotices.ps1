[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [string]$CargoPath='cargo', [string]$RustcPath='rustc')
$ErrorActionPreference='Stop'
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Notice output must be new; existing evidence is preserved.'}
$cargo=(Get-Command $CargoPath -ErrorAction Stop).Source
$rustc=(Get-Command $RustcPath -ErrorAction Stop).Source
$manifest=Join-Path $PSScriptRoot 'Cargo.toml'; $lock=Join-Path $PSScriptRoot 'Cargo.lock'
$lockHash=(Get-FileHash -LiteralPath $lock).Hash
$lockText=Get-Content -LiteralPath $lock -Raw
$checksums=@{}
foreach($block in ($lockText -split '\[\[package\]\]')){
    if($block -match '(?m)^name = "([^"]+)"'){$name=$Matches[1]}else{continue}
    if($block -match '(?m)^version = "([^"]+)"'){$version=$Matches[1]}else{continue}
    if($block -match '(?m)^checksum = "([0-9a-f]{64})"'){$checksums["$name@$version"]=$Matches[1].ToUpperInvariant()}
}
# Metadata stays in process memory. Never persist or print the machine-specific manifest paths.
$metadataText=& $cargo metadata --offline --locked --format-version 1 --filter-platform x86_64-pc-windows-msvc --manifest-path $manifest
if($LASTEXITCODE -ne 0){throw 'Locked offline Cargo metadata failed; no dependency fetch is permitted.'}
$metadata=($metadataText -join "`n")|ConvertFrom-Json
$resolved=@{}; foreach($node in $metadata.resolve.nodes){$resolved[$node.id]=$true}
$builder=[Text.StringBuilder]::new()
[void]$builder.AppendLine('Soda Asset Quick Read - complete Rust dependency license and copyright notices')
[void]$builder.AppendLine("Cargo.lock SHA256: $lockHash")
[void]$builder.AppendLine('Target: x86_64-pc-windows-msvc; locked offline resolved graph, including build dependencies.')
[void]$builder.AppendLine('Each registry archive is verified against its Cargo.lock checksum; original notice texts follow in full.')
$registryCount=0; $upstreamCount=0; $documentCount=0
foreach($package in ($metadata.packages|Where-Object {$resolved.ContainsKey($_.id)}|Sort-Object name,version)){
    if($package.name -eq 'soda-asset-capture'){continue}
    [void]$builder.AppendLine("`n======================================================================")
    [void]$builder.AppendLine("Package: $($package.name) $($package.version)")
    [void]$builder.AppendLine("Declared license: $($package.license)")
    $documents=@()
    if($package.source){
        if(-not $package.source.StartsWith('registry+')){throw "Unsupported non-registry dependency source: $($package.name)"}
        $identity="$($package.name)@$($package.version)"; $expected=$checksums[$identity]
        if(-not $expected){throw "Missing lock checksum: $identity"}
        $packageDirectory=Split-Path -Parent $package.manifest_path
        $indexDirectory=Split-Path -Parent $packageDirectory
        $registryDirectory=Split-Path -Parent (Split-Path -Parent $indexDirectory)
        $archive=Join-Path $registryDirectory ('cache/'+(Split-Path -Leaf $indexDirectory)+"/$($package.name)-$($package.version).crate")
        if(-not(Test-Path -LiteralPath $archive)){throw "Missing cached archive: $identity"}
        if((Get-FileHash -LiteralPath $archive).Hash -cne $expected){throw "Cached crate checksum mismatch: $identity"}
        [void]$builder.AppendLine("Registry archive SHA256: $expected")
        $file=[IO.File]::OpenRead($archive); $gzip=[IO.Compression.GZipStream]::new($file,[IO.Compression.CompressionMode]::Decompress); $tar=[System.Formats.Tar.TarReader]::new($gzip)
        try{
            while($entry=$tar.GetNextEntry()){
                $relative=$entry.Name.Substring($entry.Name.IndexOf('/')+1)
                if($relative -notmatch '(?i)(^|/)(LICENSE|LICENCE|COPYING|COPYRIGHT|NOTICE)([._-].*)?$' -and $relative -cne $package.license_file){continue}
                if($null -eq $entry.DataStream){if($entry.EntryType -notin @('Directory')){throw "Non-file license entry: $identity/$relative"};continue}
                $reader=[IO.StreamReader]::new($entry.DataStream,[Text.Encoding]::UTF8,$true,4096,$true)
                try{$text=$reader.ReadToEnd()}finally{$reader.Dispose()}
                $documents+=@([pscustomobject]@{name=$relative;text=$text})
            }
        }finally{$tar.Dispose();$gzip.Dispose();$file.Dispose()}
        $registryCount++
    }else{
        if($package.name -notin @('zzz-capture','zzz-crypto','zzz-gamedata','zzz-scan','zzz-wire')){throw "Unreviewed path dependency: $($package.name)"}
        $upstreamLicense=Join-Path $PSScriptRoot '.upstream/LICENSE'
        $upstreamHashes=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'upstream-files.sha256.json') -Raw|ConvertFrom-Json
        if((Get-FileHash -LiteralPath $upstreamLicense).Hash.ToLowerInvariant() -cne $upstreamHashes.LICENSE){throw 'Fixed upstream MIT license identity mismatch.'}
        [void]$builder.AppendLine('Source: pingwinekZ/zzz_packet_capture_0delay commit 4853f07c8fb5322c12b9b580d6364b577c2ac96a; shared root MIT license')
        $documents=@([pscustomobject]@{name='LICENSE';text=[IO.File]::ReadAllText($upstreamLicense)})
        $upstreamCount++
    }
    if($documents.Count -eq 0){throw "Complete license document missing: $($package.name) $($package.version)"}
    foreach($document in ($documents|Sort-Object name)){
        [void]$builder.AppendLine("`n--- BEGIN $($document.name) ---")
        [void]$builder.AppendLine($document.text)
        [void]$builder.AppendLine("--- END $($document.name) ---")
        $documentCount++
    }
}
$sysroot=(& $rustc --print sysroot).Trim(); if($LASTEXITCODE -ne 0){throw 'Fixed Rust sysroot unavailable.'}
$compilerVersion=(& $rustc --version).Trim(); if($LASTEXITCODE -ne 0 -or -not $compilerVersion.StartsWith('rustc 1.97.1 ')){throw 'Pinned Rust 1.97.1 is required for the fixed binary notices.'}
$stdlib=Join-Path $sysroot 'share/doc/rust/COPYRIGHT-library.html'
if(-not(Test-Path -LiteralPath $stdlib)){throw 'Complete Rust standard-library copyright notice missing.'}
New-Item -ItemType Directory -Path $output | Out-Null
$notice=Join-Path $output 'Rust-DEPENDENCY-NOTICES.txt'
[IO.File]::WriteAllText($notice,$builder.ToString(),[Text.UTF8Encoding]::new($false))
[IO.File]::Copy($stdlib,(Join-Path $output 'Rust-STDLIB-COPYRIGHT.html'),$false)
[ordered]@{registryPackages=$registryCount;upstreamPathPackages=$upstreamCount;fullNoticeDocuments=$documentCount;cargoLockSha256=$lockHash;rustVersion=$compilerVersion;noticesSha256=(Get-FileHash -LiteralPath $notice).Hash;standardLibraryNoticeSha256=(Get-FileHash -LiteralPath $stdlib).Hash;externalRequests=0}|ConvertTo-Json
