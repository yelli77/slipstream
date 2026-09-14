#!/bin/bash
# Sync VPS main with remote then push (rebase local merge on top)
set -e
ssh root@31.97.125.237 'cd /docker/starttruckmp/src && git pull --rebase -q origin main 2>&1 | tail -2; git log --oneline -3; git push -q origin main && echo MAIN_PUSHED'