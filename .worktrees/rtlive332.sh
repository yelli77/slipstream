#!/bin/bash
# Live test on VPS: is there a game client install on the VPS where 331 was tested? Find Slipstream/game dir
ssh root@31.97.125.237 "find / -maxdepth 3 -iname '*slipstream*' -not -path '*/tmp/slipstream-release*' 2>/dev/null | head; find /root /opt /home -maxdepth 4 -iname 'StarTruck.exe' -o -maxdepth 4 -iname 'BepInEx' -type d 2>/dev/null | head"