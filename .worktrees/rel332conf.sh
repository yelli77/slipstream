#!/bin/bash
# Inspect the conflict in the release feed repo
ssh root@31.97.125.237 "cd /tmp/slipstream-release && git status | head -20; git log --oneline origin/main -3; cat version.json"
