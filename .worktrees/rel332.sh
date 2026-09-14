#!/bin/bash
# Release 332: gzip DLL + version.json on VPS
set -e
ssh root@31.97.125.237 "cd /docker/starttruckmp/src && gzip -c bin/Release/net6.0/StarTruckMP.dll > builds/StarTruckMP-custom-build-332.dll.gz && cat > version.json << 'EOF'
{
  \"build\": \"custom-build-332\",
  \"url\": \"https://raw.githubusercontent.com/yelli77/slipstream/main/builds/StarTruckMP-custom-build-332.dll.gz\"
}
EOF
ls -la builds/StarTruckMP-custom-build-332.dll.gz && cat version.json"
