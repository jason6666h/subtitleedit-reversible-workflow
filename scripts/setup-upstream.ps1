param(
    [string]$Checkout = (Join-Path (Split-Path $PSScriptRoot -Parent) 'upstream/subtitleedit')
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$baselineTag = 'v5.2.0'
$baselineCommit = 'd8e3b8b41e856a896c541ce7e59b490a99c21196'
$repo = 'https://github.com/SubtitleEdit/subtitleedit.git'
$patch = Join-Path $root 'integration/host-hooks.patch'

if (-not (Test-Path -LiteralPath $Checkout)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $Checkout -Parent) | Out-Null
    git clone --branch $baselineTag --depth 1 $repo $Checkout
    if ($LASTEXITCODE -ne 0) { throw 'Failed to clone the supported Subtitle Edit baseline.' }
}
else {
    git -C $Checkout fetch --depth 1 origin "refs/tags/$baselineTag:refs/tags/$baselineTag" --force
    if ($LASTEXITCODE -ne 0) { throw 'Failed to refresh the supported Subtitle Edit baseline.' }
}

git -C $Checkout reset --hard $baselineCommit
if ($LASTEXITCODE -ne 0) { throw 'Failed to reset to the supported Subtitle Edit commit.' }

git -C $Checkout clean -fdx
if ($LASTEXITCODE -ne 0) { throw 'Failed to clean the Subtitle Edit checkout.' }

git -C $Checkout apply --check $patch
if ($LASTEXITCODE -ne 0) { throw 'Integration patch does not apply cleanly to the supported baseline.' }

git -C $Checkout apply $patch
if ($LASTEXITCODE -ne 0) { throw 'Failed to apply the integration patch.' }

Write-Host "Prepared Subtitle Edit $baselineTag integration checkout: $Checkout"
