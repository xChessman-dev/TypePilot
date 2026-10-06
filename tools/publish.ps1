$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$dotnetExe=if ($env:DOTNET_ROOT_X64) { Join-Path $env:DOTNET_ROOT_X64 'dotnet.exe' } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME=Join-Path $projectRoot '.build/dotnet-home'
$output=Join-Path $projectRoot 'artifacts/TypePilot-win-x64'
& $dotnetExe publish (Join-Path $projectRoot 'src/TypePilot.App/TypePilot.App.csproj') -c Release -r win-x64 --self-contained true -o $output -maxcpucount:2
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md'),(Join-Path $projectRoot 'THIRD_PARTY.md') -Destination $output
$cacheRoot=if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { ((& $dotnetExe nuget locals global-packages --list)[0] -replace '^global-packages:\s*','').Trim() }
$runtimeInfo=Get-Content -LiteralPath (Join-Path $output 'TypePilot.runtimeconfig.json') -Raw | ConvertFrom-Json
$licenseOutput=Join-Path $output 'licenses'
New-Item -ItemType Directory -Path $licenseOutput -Force | Out-Null
foreach ($framework in $runtimeInfo.runtimeOptions.includedFrameworks) {
  $package=Join-Path $cacheRoot ($framework.name.ToLowerInvariant()+'.runtime.win-x64/'+$framework.version)
  foreach ($notice in @(Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$' })) {
    Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenseOutput ($framework.name+'-'+$notice.Name+'.txt'))
  }
}
Write-Host ('Portable application: '+(Join-Path $output 'TypePilot.exe'))
