#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
version="${1:-0.3.0}"
if [[ ! "$version" =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$ ]]; then
  echo 'Expected a NuGet version such as 0.1.0 or 0.2.0-rc.1' >&2
  exit 1
fi
dotnet restore Vetta.Connector.sln
dotnet build Vetta.Connector.sln -c Release --no-restore -p:Version="$version"
dotnet test tests/Vetta.Connector.Tests -c Release --no-build --no-restore
node --test tests/browser/login.test.mjs
mkdir -p artifacts
# Keep packages from earlier local versions out of release uploads.
find artifacts -maxdepth 1 -type f \( -name "*.nupkg" -o -name "*.snupkg" \) -delete
dotnet pack src/Vetta.Connector -c Release --no-build --no-restore -p:Version="$version" -o artifacts
dotnet pack src/Vetta.Connector.AspNetCore -c Release --no-build --no-restore -p:Version="$version" -o artifacts
dotnet pack src/Vetta.Connector.SqlServer -c Release --no-build --no-restore -p:Version="$version" -o artifacts
