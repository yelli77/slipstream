#!/bin/bash
# Push 332 artifacts to public slipstream-release repo (feed) + commit
set -e
ssh root@31.97.125.237 "cp /docker/starttruckmp/src/builds/StarTruckMP-custom-build-332.dll.gz /tmp/slipstream-release/builds/ && cp /docker/starttruckmp/src/version.json /tmp/slipstream-release/version.json && cd /tmp/slipstream-release && git add -A && git commit -m 'custom-build-332: SystemSaveData nativer Union-Write (il2cpp_field_set_value) + natives Union-Gate GetMaxSizeOf(SystemSaveData)' && TOKEN=\$(grep '^GITHUB_TOKEN=' /root/mcp-server/.env | cut -d= -f2-) && git push -q https://x-access-token:\$TOKEN@github.com/yelli77/slipstream.git main:main && echo PUSHED"
