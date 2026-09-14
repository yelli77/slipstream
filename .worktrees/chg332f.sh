#!/bin/bash
# Sync VPS main branch with origin (fast-forward), remove conflict leftover, keep builds dir clean
set -e
ssh root@31.97.125.237 'cd /docker/starttruckmp/src && rm -f builds/StarTruckMP-custom-build-332.dll.gz.bak && git merge --no-edit origin/main 2>&1 | tail -3 || git reset --hard origin/main; git log --oneline -3; git push -q origin main && echo MAIN_PUSHED'