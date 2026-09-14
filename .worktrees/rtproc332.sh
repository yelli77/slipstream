#!/bin/bash
# Find the 331 test log: grep CHANGES.md head for the 331 section (test procedure)
ssh root@31.97.125.237 "cd /docker/starttruckmp/src && awk '/## custom-build-331/,/## custom-build-330/' CHANGES.md | grep -iA3 'test\|log\|roundtrip' | head -30"