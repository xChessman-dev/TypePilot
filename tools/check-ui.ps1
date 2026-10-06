$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$exe=Join-Path $projectRoot 'src/TypePilot.App/bin/Release/net10.0-windows/TypePilot.exe'
$report=Join-Path $projectRoot 'artifacts/qa/ui-check.json'
$started=Get-Date
$process=Start-Process -FilePath $exe -ArgumentList '--ui-smoke' -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(25000)) {
  $process.Kill()
  throw 'UI smoke check timed out; only its own test process was stopped.'
}
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) { throw 'UI smoke check failed.' }
if ((Get-Item -LiteralPath $report).LastWriteTime -lt $started) { throw 'UI report is stale.' }
$result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
if (-not $result.passed) { throw ('UI smoke check failed: '+$result.error) }
Write-Host 'PASS: WPF correction, undo, dictionary, native Edit contract and rendering.'
