#!/bin/bash
# List FlatSharp.Runtime classes to find the concrete SpanWriter implementation
ssh root@31.97.125.237 "CID=\$(docker ps -q --filter name=dotnet-build); docker exec \$CID sh -c 'export PATH=\$PATH:/root/.dotnet/tools; ilspycmd -l c /bepinex/interop/FlatSharp.Runtime.dll 2>/dev/null | grep -v MethodInfo | head -60'"
