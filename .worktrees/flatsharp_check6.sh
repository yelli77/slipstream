#!/bin/bash
# Find how GeneratedSerializerWrapper.Write<T> creates its writer + what ISerializerExtensions.Write does
ssh root@31.97.125.237 "CID=\$(docker ps -q --filter name=dotnet-build); docker exec \$CID sh -c 'export PATH=\$PATH:/root/.dotnet/tools; ilspycmd -t FlatSharp.Internal.SpanWriterExtensions /bepinex/interop/FlatSharp.Runtime.dll > /tmp/swe.cs 2>&1; head -80 /tmp/swe.cs'"
