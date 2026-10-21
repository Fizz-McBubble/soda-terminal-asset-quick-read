[CmdletBinding()]
param([string]$EvidenceDirectory)
$ErrorActionPreference = 'Stop'
$executable = Join-Path $PSScriptRoot 'runtime\soda-asset-capture.exe'
if ($EvidenceDirectory) {
    $testDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
} else {
    $testDirectory = Join-Path $PSScriptRoot ('outputs\offline-smoke-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
}
if (Test-Path -LiteralPath $testDirectory) { throw 'Offline evidence directory already exists; nothing was overwritten.' }
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$results = [System.Collections.Generic.List[object]]::new()
function Invoke-Probe([string]$Name, [string[]]$Arguments, [int]$ExpectedExit, [string]$ExpectedText) {
    $start = [System.Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(15000)) {
        $process.Kill()
        throw ('Offline command did not exit: ' + $Name)
    }
    $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
    $process.Dispose()
    if ($exitCode -ne $ExpectedExit -or -not $output.Contains($ExpectedText)) {
        throw ('Packaged check failed: ' + $Name + ', exit=' + $exitCode + ', output=' + $output)
    }
    $results.Add([pscustomobject]@{ check = $Name; exitCode = $exitCode; passed = $true })
}
Invoke-Probe 'inspect_no_capture' @('inspect') 0 '"liveCaptureStarted":false'
$existing = Join-Path $testDirectory 'existing.json'
[IO.File]::WriteAllText($existing, 'sentinel')
Invoke-Probe 'existing_output_blocks_capture' @('capture', '--out', $existing, '--seconds', '1') 1 'output_already_exists_capture_not_started'
Invoke-Probe 'existing_output_blocks_public_queries' @('capture', '--cn-public-bootstrap', '--out', $existing) 1 'output_already_exists_capture_not_started'
Invoke-Probe 'existing_output_blocks_configuration_only_queries' @('check-cn-config', '--out', $existing) 1 'output_already_exists_capture_not_started'
Invoke-Probe 'existing_output_blocks_local_resource_read' @('inspect-client-dispatch', '--input', (Join-Path $testDirectory 'absent\resources.assets'), '--out', $existing) 1 'output_already_exists_capture_not_started'
Invoke-Probe 'existing_output_blocks_client_configuration_queries' @('check-cn-config', '--client-resources', (Join-Path $testDirectory 'absent\resources.assets'), '--out', $existing) 1 'output_already_exists_capture_not_started'
Invoke-Probe 'existing_output_blocks_client_bootstrap_capture' @('capture', '--client-resources', (Join-Path $testDirectory 'absent\resources.assets'), '--out', $existing) 1 'output_already_exists_capture_not_started'
if ([IO.File]::ReadAllText($existing) -ne 'sentinel') { throw 'Existing output changed.' }
Invoke-Probe 'missing_parent_blocks_capture' @('capture', '--out', (Join-Path $testDirectory 'missing\report.json')) 1 'output_parent_missing_capture_not_started'
Invoke-Probe 'missing_parent_blocks_configuration_only_queries' @('check-cn-config', '--out', (Join-Path $testDirectory 'missing\configuration.json')) 1 'output_parent_missing_capture_not_started'
$clientFixture = Join-Path $testDirectory 'synthetic-client'
New-Item -ItemType Directory -Path $clientFixture | Out-Null
$resources = Join-Path $clientFixture 'resources.assets'
[IO.File]::WriteAllText($resources, '{"DispatchSeed":"0123456789ABCDEF","DispatchVersion":"CNPRODWin3.2.0","Ignored":"SYNTHETIC_UNSELECTED_MARKER"}')
$fieldReceipt = Join-Path $testDirectory 'client-fields.json'
Invoke-Probe 'synthetic_client_fields_without_protocol_or_network' @('--assets', (Join-Path $testDirectory 'absent-protocol'), 'inspect-client-dispatch', '--input', $resources, '--out', $fieldReceipt) 0 '"requestsAttempted":0'
$safeFieldText = [IO.File]::ReadAllText($fieldReceipt)
if ($safeFieldText.Contains('0123456789ABCDEF') -or $safeFieldText.Contains('SYNTHETIC_UNSELECTED_MARKER')) { throw 'Local field receipt leaked a value.' }
$localFields = $safeFieldText | ConvertFrom-Json
if ($localFields.requestSeedSaved -or $localFields.rawFileSaved -or $localFields.liveCaptureStarted -or $localFields.unselectedFieldsExported) { throw 'Local inspection enabled a forbidden output.' }
[IO.File]::WriteAllText($resources, '{"DispatchSeed":"0123456789ABCDEF","DispatchVersion":"OSPRODWin3.2.0"}')
$wrongClientReceipt = Join-Path $testDirectory 'wrong-client-version.json'
Invoke-Probe 'wrong_client_version_stops_before_queries' @('check-cn-config', '--client-resources', $resources, '--out', $wrongClientReceipt) 1 'client_dispatch_version_rejected'
$wrongClient = Get-Content -LiteralPath $wrongClientReceipt -Raw | ConvertFrom-Json
if ($wrongClient.public_metadata.requests_attempted -ne 0) { throw 'Wrong client issued a request.' }
[IO.File]::WriteAllText($resources, '{"DispatchVersion":"CNPRODWin3.2.0","DispatchSeed":"0123456789ABCDEF","DispatchSeed":"0123456789ABCDEF"}')
$duplicateClientReceipt = Join-Path $testDirectory 'duplicate-client-fields.json'
Invoke-Probe 'ambiguous_client_fields_stop_before_queries' @('check-cn-config', '--client-resources', $resources, '--out', $duplicateClientReceipt) 1 'client_dispatch_fields_missing_or_ambiguous'
$duplicateClient = Get-Content -LiteralPath $duplicateClientReceipt -Raw | ConvertFrom-Json
if ($duplicateClient.public_metadata.requests_attempted -ne 0) { throw 'Ambiguous client issued a request.' }
Invoke-Probe 'unsupported_client_filename_rejected' @('inspect-client-dispatch', '--input', $existing, '--out', (Join-Path $testDirectory 'wrong-source-name.json')) 1 'client_resource_input_rejected'
Invoke-Probe 'public_bootstrap_requires_cn_before_queries' @('capture', '--cn-public-bootstrap', '--game-region', 'OS', '--out', (Join-Path $testDirectory 'wrong-region.json')) 1 'public_cn_bootstrap_requires_cn_region'
Invoke-Probe 'client_bootstrap_requires_cn_before_queries' @('capture', '--client-resources', (Join-Path $testDirectory 'absent\resources.assets'), '--game-region', 'OS', '--out', (Join-Path $testDirectory 'wrong-client-region.json')) 1 'public_cn_bootstrap_requires_cn_region'
Invoke-Probe 'client_bootstrap_conflicts_with_generated_parameter_mode' @('capture', '--client-resources', (Join-Path $testDirectory 'absent\resources.assets'), '--cn-public-bootstrap', '--out', (Join-Path $testDirectory 'conflicting-modes.json')) 2 'cannot be used with'
$emptyReplay = Join-Path $testDirectory 'empty-synthetic.json'
[IO.File]::WriteAllText($emptyReplay, '{"packets":[]}')
$emptyReport = Join-Path $testDirectory 'empty-report.json'
Invoke-Probe 'empty_replay_stays_incomplete' @('replay', '--input', $emptyReplay, '--out', $emptyReport) 2 'handshake_not_observed'
$report = Get-Content -LiteralPath $emptyReport -Raw | ConvertFrom-Json
if ($report.ready_for_account_import -or $report.full_inventory_verified -or $report.raw_traffic_saved -or
    $report.account_write_enabled -or $report.coverage.disc_load_received) { throw 'Empty replay was promoted.' }
$invalidReplay = Join-Path $testDirectory 'invalid-synthetic.json'
[IO.File]::WriteAllText($invalidReplay, '{"packets":[{"direction":256,"timestamp":1,"data":[]}]}')
Invoke-Probe 'invalid_direction_rejected' @('replay', '--input', $invalidReplay, '--out', (Join-Path $testDirectory 'invalid-report.json')) 1 'invalid_replay_direction'
$alteredProtocol = Join-Path $testDirectory 'altered-protocol'
New-Item -ItemType Directory -Path $alteredProtocol | Out-Null
foreach ($file in @('manifest.json', 'datamine.json', 'nap.json')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('runtime\assets\' + $file)) -Destination $alteredProtocol
}
[IO.File]::AppendAllText((Join-Path $alteredProtocol 'nap.json'), ' ')
Invoke-Probe 'protocol_drift_rejected' @('--assets', $alteredProtocol, 'inspect') 1 'protocol_hash_mismatch:nap.json'
$summary = [pscustomobject]@{ schema = 'soda-asset-capture-offline-smoke/v1'; checks = $results;
    executableSha256 = (Get-FileHash -LiteralPath $executable).Hash; liveCaptureStarted = $false }
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testDirectory 'verification.json') -Encoding utf8
[pscustomobject]@{ passed = $results.Count; verification = (Join-Path $testDirectory 'verification.json'); liveCaptureStarted = $false } | ConvertTo-Json
# Expected rejection commands must not leave a misleading native failure code
# in a calling PowerShell build/check wrapper after all assertions passed.
$global:LASTEXITCODE = 0
