#!/usr/bin/env bash
# Builds Driftwood in Release. Release is the only configuration we verify against.
set -u
cd "$(dirname "$0")"

if ! dotnet build Driftwood.sln -c Release --nologo; then
  echo
  echo "BUILD FAILED"
  exit 1
fi

echo
echo "Built: src/Driftwood.Client/bin/Release/net11.0/Driftwood"
