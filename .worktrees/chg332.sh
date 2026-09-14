#!/bin/bash
# Update CHANGES.md with 332 section on VPS tree, commit + push to source repo, merge to main
ssh root@31.97.125.237 "cd /docker/starttruckmp/src && git branch --show-current && git status --porcelain | head -3"