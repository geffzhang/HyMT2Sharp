# Publishes HyMT2Sharp.McpServer as a single-file executable.
#
# Usage:
#   ./scripts/publish-mcpserver.ps1                      # framework-dependent (needs .NET 10 runtime)
#   ./scripts/publish-mcpserver.ps1 -SelfContained        # self-contained, requires -RID
#   ./scripts/publish-mcpserver.ps1 -RID win-x64 -SelfContained
#
# Output: publish/mcp-server/  (exe + ui/translate.html next to it)
param(
    [string]$RID = "",
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\HyMT2Sharp.McpServer\HyMT2Sharp.McpServer.csproj"
$outDir = Join-Path $repoRoot "publish\mcp-server"

if ($SelfContained -and -not $RID) {
    throw "-SelfContained requires -RID (e.g. win-x64, linux-x64, osx-arm64)"
}

$extraArgs = @()
if ($RID) { $extraArgs += "-r", $RID }
if ($SelfContained) { $extraArgs += "--self-contained", "true" }

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

dotnet publish $project -c Release `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $outDir `
    @extraArgs

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Get-ChildItem $outDir -Filter "*.exe" | Select-Object -First 1
if (-not $exe) { $exe = Get-ChildItem $outDir -Filter "HyMT2Sharp.McpServer" | Select-Object -First 1 }
if (-not $exe) { throw "published executable not found in $outDir" }

Write-Host ""
Write-Host "Published: $($exe.FullName)"
Write-Host "UI assets: $(Join-Path $outDir 'ui\translate.html') -> $(Test-Path (Join-Path $outDir 'ui\translate.html'))"
Write-Host ""
Write-Host "Run (stdio):  $($exe.FullName) --model <path-to-gguf>"
Write-Host "Run (http):   $($exe.FullName) --http --port 17890 --model <path-to-gguf>"
