#!/bin/bash
# Read the 331 section of CHANGES.md from VPS (head of file)
ssh root@31.97.125.237 "cd /docker/starttruckmp/src && sed -n 1,60p CHANGES.md"