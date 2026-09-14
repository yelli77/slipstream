#!/bin/bash
# Look at gsw2.cs content
ssh root@31.97.125.237 "CID=\$(docker ps -q --filter name=dotnet-build); docker exec \$CID sh -c 'head -40 /tmp/gsw2.cs; echo ...; grep -n \"Writer\\|writer\" /tmp/gsw2.cs | head -30'"
