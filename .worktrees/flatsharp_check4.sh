#!/bin/bash
# Inspect GeneratedSerializerWrapper: ctor + how Write<T> creates the span writer
ssh root@31.97.125.237 "CID=\$(docker ps -q --filter name=dotnet-build); docker exec \$CID sh -c 'export PATH=\$PATH:/root/.dotnet/tools; ilspycmd -t FlatSharp.GeneratedSerializerWrapper /bepinex/interop/FlatSharp.Runtime.dll > /tmp/gsw2.cs 2>&1; grep -n \"ctor\\|SpanWriter\\|CreateWriter\\|InvokeWrite\" /tmp/gsw2.cs | head -20'"
