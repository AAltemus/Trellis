#!/usr/bin/env bash
# First-time setup on macOS/Linux: restore tools, create the initial EF migration (once), build, test.
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet --version
dotnet tool restore

if [ ! -d src/GuitarPractice.Infrastructure/Persistence/Migrations ]; then
  dotnet ef migrations add InitialCreate \
    -p src/GuitarPractice.Infrastructure \
    -s src/GuitarPractice.Infrastructure \
    -o Persistence/Migrations
fi

dotnet build GuitarPractice.slnx -c Debug
dotnet test GuitarPractice.slnx -c Debug --no-build
echo "Done. Run the app with: dotnet run --project src/GuitarPractice.App"
