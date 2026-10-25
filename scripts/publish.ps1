param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\VOIDOverlay.Tacho\VOIDOverlay.Tacho.csproj"
$output = Join-Path $repoRoot "artifacts\publish\win-x64"
$outputParent = Split-Path -Parent $output
$runId = [Guid]::NewGuid().ToString("N")
$staging = Join-Path $outputParent "publish.staging-$runId"
$backup = Join-Path $outputParent "publish.backup-$runId"
$locationPushed = $false

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK 10 is required. Install the .NET 10 SDK and retry."
}

New-Item -ItemType Directory -Path $outputParent -Force | Out-Null
try {
    Push-Location $repoRoot
    $locationPushed = $true
    try {
        dotnet publish $project --configuration $Configuration --runtime win-x64 --self-contained false `
            -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false --output $staging
        if ($LASTEXITCODE -ne 0) {
            throw "Windows x64 publish failed with exit code $LASTEXITCODE."
        }
        foreach ($file in @(
            "VOIDOverlay.Tacho.exe",
            "VOIDOverlay.Tacho.dll",
            "VOIDOverlay.Tacho.deps.json",
            "VOIDOverlay.Tacho.runtimeconfig.json"
        )) {
            if (-not (Test-Path -LiteralPath (Join-Path $staging $file) -PathType Leaf)) {
                throw "The publish output is missing required file '$file'."
            }
        }
        if (Get-ChildItem -LiteralPath $staging -Recurse -File -Filter "*.pdb") {
            throw "The publish output contains development symbols."
        }

        $hadPreviousOutput = Test-Path -LiteralPath $output
        $previousOutputMoved = $false
        try {
            if ($hadPreviousOutput) {
                Move-Item -LiteralPath $output -Destination $backup
                $previousOutputMoved = $true
            }
            Move-Item -LiteralPath $staging -Destination $output
        }
        catch {
            if ((-not $hadPreviousOutput -or $previousOutputMoved) -and (Test-Path -LiteralPath $output)) {
                Remove-Item -LiteralPath $output -Recurse -Force
            }
            if ($previousOutputMoved -and (Test-Path -LiteralPath $backup)) {
                Move-Item -LiteralPath $backup -Destination $output
            }
            throw
        }
        if (Test-Path -LiteralPath $backup) {
            Remove-Item -LiteralPath $backup -Recurse -Force
        }
    }
    finally {
        if (Test-Path -LiteralPath $staging) {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
    }
}
finally {
    if ($locationPushed) { Pop-Location }
}

Write-Output "Framework-dependent Windows x64 publish: $output"
