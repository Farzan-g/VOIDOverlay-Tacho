param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "VOIDOverlay.Tacho.sln"
$artifacts = Join-Path $repoRoot "artifacts\build"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK 10 is required. Install the .NET 10 SDK and retry."
}

Push-Location $repoRoot
try {
    dotnet build $project --configuration $Configuration --artifacts-path $artifacts
    if ($LASTEXITCODE -ne 0) {
        throw "$Configuration build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
