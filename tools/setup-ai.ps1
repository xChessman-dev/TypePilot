param([string]$Destination='F:/DevTools/TypePilotAI')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($Destination)
if ([IO.Path]::GetPathRoot($root).TrimEnd('\','/') -ne 'F:') { throw 'Use a dedicated directory on drive F.' }
if ($root.TrimEnd('\','/') -eq 'F:') { throw 'The drive root is not a valid destination.' }
$downloads=Join-Path $root 'downloads'
$runtime=Join-Path $root 'runtime'
$models=Join-Path $root 'models'
New-Item -ItemType Directory -Path $downloads,$runtime,$models -Force | Out-Null
function Get-VerifiedFile([string]$Url,[string]$Path,[string]$Sha256) {
  if ((Test-Path -LiteralPath $Path) -and (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $Sha256) { return }
  $partial=$Path+'.partial'
  & curl.exe --fail --location --retry 3 --output $partial $Url
  if ($LASTEXITCODE -ne 0) { throw 'Download failed. No executable was launched.' }
  if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Sha256) { throw 'SHA-256 mismatch. File was not installed.' }
  if (Test-Path -LiteralPath $Path) { throw ('Existing file differs; preserve it and choose a fresh directory: '+$Path) }
  Move-Item -LiteralPath $partial -Destination $Path
}
$runtimeZip=Join-Path $downloads 'llama-b11429-bin-win-vulkan-x64.zip'
Get-VerifiedFile 'https://github.com/ggml-org/llama.cpp/releases/download/b11429/llama-b11429-bin-win-vulkan-x64.zip' $runtimeZip '1bfe78ad9168b79fa02bf67f6af9f5e17a966d824d77238517f7bef12ac73b36'
if (-not (Test-Path -LiteralPath (Join-Path $runtime 'llama-server.exe'))) {
  if ((Get-ChildItem -LiteralPath $runtime -Force | Measure-Object).Count -ne 0) { throw 'Runtime directory is not empty; it will not be overwritten.' }
  Expand-Archive -LiteralPath $runtimeZip -DestinationPath $runtime
}
Get-VerifiedFile 'https://huggingface.co/Qwen/Qwen3-4B-GGUF/resolve/bc640142c66e1fdd12af0bd68f40445458f3869b/Qwen3-4B-Q4_K_M.gguf' (Join-Path $models 'Qwen3-4B-Q4_K_M.gguf') '7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5'
Write-Host ('Ready: '+$root)
