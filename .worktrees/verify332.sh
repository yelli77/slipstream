#!/bin/bash
# Verify build number via python in container
ssh root@31.97.125.237 "docker exec starttruckmp-dotnet-build-1 python3 -c \"
data = open('/src/bin/Release/net6.0/StarTruckMP.dll','rb').read()
import re
print(sorted(set(re.findall(rb'custom-build-3\\\\d+'.encode(), data))))
\" 2>/dev/null || docker exec starttruckmp-dotnet-build-1 bash -lc 'grep -aoE \"custom-build-3[0-9]+\" /src/bin/Release/net6.0/StarTruckMP.dll | sort -u'"
