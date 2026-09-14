#!/bin/bash
# Resolve version.json conflict -> 332, continue rebase, push
set -e
ssh root@31.97.125.237 "cd /tmp/slipstream-release && cat > version.json << 'EOF'
{
  \"build\": \"custom-build-332\",
  \"url\": \"https://raw.githubusercontent.com/yelli77/slipstream/main/builds/StarTruckMP-custom-build-332.dll.gz\"
}
EOF
git add version.json && git -c core.editor=true rebase --continue && TOKEN=\$(grep '^GITHUB_TOKEN=' /root/mcp-server/.env | cut -d= -f2-) && git push -q https://x-access-token:\$TOKEN@github.com/yelli77/slipstream.git main:main && echo PUSHED && cat version.json"
