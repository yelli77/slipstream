#!/bin/bash
# VPS main: save .gz aside, hard reset to origin/main, restore .gz
set -e
ssh root@31.97.125.237 'cd /docker/starttruckmp/src && cp builds/StarTruckMP-custom-build-332.dll.gz /tmp/332.dll.gz && git checkout -- bin obj 2>/dev/null; git reset --hard -q origin/main && cp /tmp/332.dll.gz builds/ && git log --oneline -2 && echo SYNCED'