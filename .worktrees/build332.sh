#!/bin/bash
# Pull branch on VPS and run dotnet build in container; report errors only
set -e
ssh root@31.97.125.237 "cd /docker/starttruckmp/src && git pull -q && git log --oneline -1"
ssh root@31.97.125.237 "docker exec starttruckmp-dotnet-build-1 bash -lc 'cd /src && dotnet build StarTruckMP.csproj -c Release 2>&1' | grep -E 'error|Build succeeded' | head -8"
