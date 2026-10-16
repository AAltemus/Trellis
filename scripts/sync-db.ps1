# Keeps EF migrations in step with the model. Runs before every F5 launch (VS Code preLaunchTask "sync-db").
#  - No migrations yet      -> creates InitialCreate
#  - Model changed          -> adds a migration named Auto_<timestamp>
#  - Model unchanged        -> does nothing
# The app applies pending migrations on startup (after backing up the database), so the next launch is up to date.
# Run manually:  powershell -ExecutionPolicy Bypass -File scripts\sync-db.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

$proj = 'src/GuitarPractice.Infrastructure'
$efArgs = @('-p', $proj, '-s', $proj, '--no-build')

dotnet tool restore | Out-Null
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

dotnet build $proj -nologo -v q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not (Test-Path "$proj/Persistence/Migrations")) {
    Write-Host 'No migrations found; creating InitialCreate.' -ForegroundColor Yellow
    dotnet ef migrations add InitialCreate @efArgs -o Persistence/Migrations
    exit $LASTEXITCODE
}

# Exit code 0 = no changes; non-zero = changes pending (or an error, which we surface).
# Windows PowerShell 5.1 turns each stderr line of a redirected native command into an ErrorRecord, which 'Stop'
# makes fatal (NuGet audit warnings alone would kill the script), so relax it for this call.
$ErrorActionPreference = 'Continue'
$output = dotnet ef migrations has-pending-model-changes @efArgs 2>&1 | Out-String
$pendingExit = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($pendingExit -eq 0) {
    Write-Host 'Database model is up to date.' -ForegroundColor Green
    exit 0
}
if ($output -cnotmatch 'Changes have been made') {
    Write-Host $output
    Write-Host 'FAILED: could not check for model changes.' -ForegroundColor Red
    exit 1
}

$name = 'Auto_' + (Get-Date -Format 'yyyyMMdd_HHmmss')
Write-Host "Model changed; adding migration $name." -ForegroundColor Yellow
dotnet ef migrations add $name @efArgs -o Persistence/Migrations
exit $LASTEXITCODE
