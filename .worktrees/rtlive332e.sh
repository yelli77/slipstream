#!/bin/bash
# Check recent dedicated server logs (does it run StarTruckMP.Dedicated as relay?) and find how 331 live test ran — check docker-compose
ssh root@31.97.125.237 "CID=\$(docker ps -q --filter name=dedicated); docker logs --tail 20 \$CID 2>&1 | tail -20"