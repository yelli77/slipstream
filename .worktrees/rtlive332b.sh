#!/bin/bash
# Check what /tmp/slipstream-311.bundle is (a game client copy used for testing?) and look for a Unity player on the VPS
ssh root@31.97.125.237 "file /tmp/slipstream-311.bundle 2>/dev/null; ls /tmp/slipstream-public | head; find / -maxdepth 4 -iname 'StarTruck*.exe' -not -path '/proc/*' -not -path '/sys/*' 2>/dev/null | head"