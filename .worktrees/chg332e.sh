#!/bin/bash
# Check VPS main state: which branch, does remote main have 332?
set -e
ssh root@31.97.125.237 'cd /docker/starttruckmp/src && git branch --show-current; git fetch -q origin && git log --oneline origin/main -4; git log --oneline -1'