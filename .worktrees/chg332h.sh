#!/bin/bash
# Rebase local VPS main (5799849 CHANGES) onto origin/main (2fbafcf = my squashed commit), push
set -e
ssh root@31.97.125.237 'cd /docker/starttruckmp/src && git stash -q 2>/dev/null; git pull --rebase -q origin main 2>&1 | tail -2; git log --oneline -3; git push -q origin main && echo MAIN_PUSHED; git stash pop -q 2>/dev/null; true'