#!/bin/bash
# Where does the game itself run for live testing? Check dedicated server compose + whether a client test rig exists (wine/proton/xvfb?)
ssh root@31.97.125.237 "which wine xvfb-run 2>/dev/null; ls /root | head -20; docker inspect starttruckmp-dedicated-1 --format '{{.Config.Image}} {{.HostConfig.Binds}}'"