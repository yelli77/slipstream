#!/bin/bash
# Look at ISerializerExtensions.Write(ISerializer<T>, Il2CppStructArray<byte>, T) body - how it creates the writer
ssh root@31.97.125.237 "CID=\$(docker ps -q --filter name=dotnet-build); docker exec \$CID sh -c 'export PATH=\$PATH:/root/.dotnet/tools; ilspycmd -t FlatSharp.ISerializerExtensions /bepinex/interop/FlatSharp.Runtime.dll > /tmp/ise2.cs 2>&1; grep -n \"public unsafe static int Write<T>\" /tmp/ise2.cs'"
