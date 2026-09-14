#!/bin/bash
# Inspect SpanWriter class list (all) + MemoryImpulseSpanWriter ctor
ssh root@31.97.125.237 "CID=\$(docker ps -q --filter name=dotnet-build); docker exec \$CID sh -c 'export PATH=\$PATH:/root/.dotnet/tools; ilspycmd -l c /bepinex/interop/FlatSharp.Runtime.dll 2>/dev/null | grep -iE \"Writer\" | grep -v MethodInfo | grep -v Shared | head -20'"
