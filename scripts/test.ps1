$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$checkout = Join-Path $root 'upstream/subtitleedit'

if (-not (Test-Path -LiteralPath $checkout)) { & (Join-Path $PSScriptRoot 'setup-upstream.ps1') }

dotnet restore (Join-Path $checkout 'tests/UI/UITests.csproj') -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet run --project (Join-Path $root 'tests/SynchronousAudio.Tests/SynchronousAudio.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project (Join-Path $root 'tests/SubtitleReview.Tests/SubtitleReview.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet run --project (Join-Path $root 'tests/SubtitleReview.Session.Tests/SubtitleReview.Session.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$filter = 'FullyQualifiedName~AudioRangeSelectionTests|FullyQualifiedName~SynchronousAudioIntegrationTests|FullyQualifiedName~SubtitleReviewIntegrationTests'
dotnet test (Join-Path $checkout 'tests/UI/UITests.csproj') --no-restore -c Release --filter $filter
exit $LASTEXITCODE
