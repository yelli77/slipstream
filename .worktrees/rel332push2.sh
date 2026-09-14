#!/bin/bash
# Pull --rebase on the release feed repo and push again
set -e
ssh root@31.97.125.237 "cd /tmp/slipstream-release && git pull --rebase -q origin main 2>&1 | tail -2; git log --oneline -3; TOKEN=\$(grep '^GITHUB_TOKEN=' /root/mcp-server/.env | cut -d= -f2-) && git push -q https://x-access-token:\$TOKEN@github.com/yelli77/slipstream.git main:main && echo PUSHED"
