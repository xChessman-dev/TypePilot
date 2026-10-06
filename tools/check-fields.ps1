$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$exe=Join-Path $projectRoot 'src/TypePilot.App/bin/Release/net10.0-windows/TypePilot.exe'
$report=Join-Path $projectRoot 'artifacts/qa/field-check.json'
$started=Get-Date
$process=Start-Process -FilePath $exe -ArgumentList '--field-smoke' -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Field check timed out; stopped only its own test process.' }
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report) -or (Get-Item -LiteralPath $report).LastWriteTime -lt $started) { throw 'Field integration check failed or report is stale.' }
$result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if ($result.skipped) { Write-Warning 'SKIP: Windows denied fixture foreground activation. No user field was read or changed. Close TypePilot and rerun in an interactive desktop session.'; exit 0 }
if (-not $result.passed) { throw ('Field check failed: '+$result.error) }
Write-Host 'PASS: UIA caret, input, undo, no focus steal, password/read-only denial, selected-range replacement and stale-source refusal.'
