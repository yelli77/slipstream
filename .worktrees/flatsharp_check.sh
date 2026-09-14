#!/bin/bash
# Inspect FlatSharp.Runtime interop: ISerializer<T> methods + SpanWriter classes
ssh root@31.97.125.237 "CID=\$(docker ps -q --filter name=dotnet-build); docker exec \$CID sh -c 'export PATH=\$PATH:/root/.dotnet/tools; ilspycmd -t FlatSharp.ISerializer /bepinex/interop/FlatSharp.Runtime.dll 2>/dev/null | grep -n \"GetMaxSize\\|Write\" | head -10; ilspycmd -l c /bepinex/interop/FlatSharp.Runtime.dll 2>/dev/null | grep -iE \"SpanWriter\" | head -10'"
