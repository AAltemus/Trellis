# First-time setup: restore tools, create the initial EF migration (once), build, test.
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File scripts\bootstrap.ps1
# -Reset deletes the migrations and your local database, then rebuilds both from the current model.
#        Use it while the schema is still changing and there is no practice history worth keeping.
param([switch]$Reset)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

if ($Reset) {
    Write-Host "`n== Reset: removing migrations and local database ==" -ForegroundColor Yellow
    Remove-Item -Recurse -Force 'src/GuitarPractice.Infrastructure/Persistence/Migrations' -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path $env:LOCALAPPDATA 'GuitarPractice\practice.db*') -ErrorAction SilentlyContinue
}

function Invoke-Step([string]$Title, [scriptblock]$Command) {
    Write-Host "`n== $Title ==" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { Write-Host "FAILED: $Title" -ForegroundColor Red; exit $LASTEXITCODE }
}

Invoke-Step 'SDK check' { dotnet --version }
Invoke-Step 'Restore local tools (dotnet-ef)' { dotnet tool restore }

if (-not (Test-Path 'src/GuitarPractice.Infrastructure/Persistence/Migrations')) {
    Invoke-Step 'Create InitialCreate migration' {
        dotnet ef migrations add InitialCreate `
            -p src/GuitarPractice.Infrastructure `
            -s src/GuitarPractice.Infrastructure `
            -o Persistence/Migrations
    }
}

Invoke-Step 'Build' { dotnet build GuitarPractice.slnx -c Debug }
Invoke-Step 'Test' { dotnet test GuitarPractice.slnx -c Debug --no-build }

Write-Host "`nDone. Run the app with:  dotnet run --project src/GuitarPractice.App" -ForegroundColor Green
