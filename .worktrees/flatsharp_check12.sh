#!/bin/bash
# Show the Write<T>(ISerializer<T>, Il2CppStructArray<byte>, T) body to see the writer instantiation
ssh root@31.97.125.237 "CID=\$(docker ps -q --filter name=dotnet-build); docker exec \$CID sh -c 'sed -n 329,370p /tmp/ise2.cs'"
