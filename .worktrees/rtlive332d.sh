#!/bin/bash
# Dedicated server binds (name resolution issue earlier was transient) + how game client connects
ssh root@31.97.125.237 "docker ps -q --filter name=dedicated | head -1 | xargs -I{} docker inspect {} --format '{{.HostConfig.Binds}}'"