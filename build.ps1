# Builds SchemaScope end to end and produces dist\SchemaScope.exe
#
#   .\build.ps1              full build
#   .\build.ps1 -SkipWeb     C# only (reuses the last front end build)
#
# The order matters: the React app is built first because it is embedded into
# the API assembly, which is then baked into the single file exe.

param(
    [switch]$SkipWeb,
    # Stamped into the exe. The release workflow passes the tag so the version
    # Explorer reports always matches the release it came from.
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Step($text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

if (-not $SkipWeb) {
    Step "Building the front end"
    Push-Location (Join-Path $root 'web')
    try {
        if (-not (Test-Path 'node_modules')) {
            Write-Host "installing npm packages (first run only)..."
            npm install
            if ($LASTEXITCODE -ne 0) { throw "npm install failed" }
        }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "npm run build failed" }
    }
    finally {
        Pop-Location
    }
}

Step "Publishing the desktop app"
$versionArgs = if ($Version) {
    @("-p:Version=$Version", "-p:AssemblyVersion=$Version.0", "-p:FileVersion=$Version.0")
} else { @() }

dotnet publish (Join-Path $root 'src\SchemaScope.Shell\SchemaScope.Shell.csproj') `
    -c Release -o (Join-Path $root 'dist') @versionArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$exe = Join-Path $root 'dist\SchemaScope.exe'
$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)

# Size budget (plan D12): the drivers and the PostgreSQL parser ship inside the exe, so growth is watched here.
# 70 MB as the plan counts it (decimal, like its 65.8 MB baseline), which is 66.8 MB the way $sizeMb counts.
$budgetBytes = 70000000
$bytes = (Get-Item $exe).Length
if ($bytes -gt $budgetBytes) {
    throw "SchemaScope.exe is $bytes bytes, over the size budget of $budgetBytes bytes (70 MB)"
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  $exe  ($sizeMb MB)"
Write-Host ""
Write-Host "  Self contained: no .NET install needed on the target machine."
Write-Host "  The UI is embedded in the exe - nothing else has to travel with it."
