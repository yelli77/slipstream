#!/bin/bash
# Look for the game client used for 331 testing: is there a Windows game / wine on the VPS? Check startruckmp-starttruckmp-1 (game server?) and previous session notes
ssh root@31.97.125.237 "docker exec starttruckmp-starttruckmp-1 sh -c 'ls /; ls /bepinex 2>/dev/null | head' 2>&1 | head -20"
