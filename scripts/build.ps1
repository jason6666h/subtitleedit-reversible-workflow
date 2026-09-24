param(
    [string]$Output = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/SubtitleEdit-win-x64')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$checkout = Join-Path $root 'upstream/subtitleedit'

if (-not (Test-Path -LiteralPath $checkout)) { & (Join-Path $PSScriptRoot 'setup-upstream.ps1') }
& (Join-Path $PSScriptRoot 'test.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (Test-Path -LiteralPath $Output) { Remove-Item -Recurse -Force -LiteralPath $Output }
dotnet publish (Join-Path $checkout 'src/ui/UI.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:NuGetAudit=false -o $Output
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Built source-integrated Subtitle Edit package: $Output"
Write-Warning 'FFmpeg and libmpv redistribution is intentionally not handled by this repository. Review their licenses before creating a binary release.'
