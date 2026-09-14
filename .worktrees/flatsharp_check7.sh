#!/bin/bash
# ISerializerExtensions.Write<T>: how the serializer wrapper creates the writer instance
ssh root@31.97.125.237 "CID=\$(docker ps -q --filter name=dotnet-build); docker exec \$CID sh -c 'export PATH=\$PATH:/root/.dotnet/tools; ilspycmd -t FlatSharp.ISerializerExtensions /bepinex/interop/FlatSharp.Runtime.dll > /tmp/ise.cs 2>&1; grep -n -B3 -A25 \"static.*Write\" /tmp/ise.cs | head -60'"
