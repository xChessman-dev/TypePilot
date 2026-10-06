$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$dotnetExe=if ($env:DOTNET_ROOT_X64) { Join-Path $env:DOTNET_ROOT_X64 'dotnet.exe' } elseif (Test-Path 'F:/DevTools/dotnet/dotnet.exe') { 'F:/DevTools/dotnet/dotnet.exe' } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_ROOT_X64=Split-Path -Parent $dotnetExe
$env:DOTNET_CLI_HOME=Join-Path $projectRoot '.build/dotnet-home'
$exe=Join-Path $projectRoot 'src/TypePilot.App/bin/Release/net10.0-windows/TypePilot.exe'
if (-not (Test-Path -LiteralPath $exe)) { & (Join-Path $PSScriptRoot 'build.ps1') }
& $exe
