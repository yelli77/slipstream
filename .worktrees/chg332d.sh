#!/bin/bash
# VPS main: stash bin artifacts, pull --rebase, pop, push
set -e
ssh root@31.97.125.237 'cd /docker/starttruckmp/src && git stash -q && git pull --rebase -q origin main && git push -q origin main && git stash pop -q 2>/dev/null; echo MAIN_PUSHED; git log --oneline -4'