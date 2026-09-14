#!/bin/bash
# Read the 331 section of CHANGES.md from VPS
ssh root@31.97.125.237 "cd /docker/starttruckmp/src && grep -n 'custom-build-331' CHANGES.md | head -2 && sed -n \"\$(grep -n '## custom-build-331' CHANGES.md | head -1 | cut -d: -f1),+40p\" CHANGES.md"