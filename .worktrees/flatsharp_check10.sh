#!/bin/bash
# Find ISerializerExtensions.Write wrapper body (SpanWriter instantiation) in the game interop dump
ssh root@31.97.125.237 "CID=\$(docker ps -q --filter name=dotnet-build); docker exec \$CID sh -c 'grep -n \"Public_Static_Int32_ISerializer_1_T_Il2CppStructArray_1_Byte_T_0<T>.Pointer, (System.IntPtr)0\" /tmp/ssc_full.cs | head; grep -n \"MemoryImpulseSpanWriter\\|SpanWriter writer =\\|new.*SpanWriter\\|CreateSpanWriter\" /tmp/ssc_full.cs | head'"
