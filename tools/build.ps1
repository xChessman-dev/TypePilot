param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$dotnetExe=if ($env:DOTNET_ROOT_X64) { Join-Path $env:DOTNET_ROOT_X64 'dotnet.exe' } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:DOTNET_CLI_HOME=Join-Path $projectRoot '.build/dotnet-home'
Push-Location $projectRoot
try {
  & $dotnetExe build TypePilot.slnx -c $Configuration -maxcpucount:2
  if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
  & $dotnetExe run --project tests/TypePilot.Tests -c $Configuration --no-build
  if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
} finally { Pop-Location }
