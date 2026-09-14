#!/bin/bash
# Find where 331 roundtrip was tested: check dedicated server logs dir + game server container
ssh root@31.97.125.237 "docker logs startruckmp-dedicated-1 2>&1 | grep -i 'roundtrip' | tail -5; docker logs starttruckmp-starttruckmp-1 2>&1 | grep -i 'roundtrip' | tail -5"
