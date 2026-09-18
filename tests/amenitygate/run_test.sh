#!/usr/bin/env bash
# AmenityLocalGate (368): stub-compile + logic test (no interop assemblies needed).
set -u
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
cd "$(dirname "$0")"
/opt/data/.dotnet/dotnet build AmenityGateTest.csproj -c Release 2>&1 | tail -20
echo "--- RUN ---"
/opt/data/.dotnet/dotnet bin/Release/net6.0/AmenityGateTest.dll 2>&1
