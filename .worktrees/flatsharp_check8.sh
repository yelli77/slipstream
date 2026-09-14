#!/bin/bash
# Show the generated Write<T>(ISerializer<T>, Il2CppStructArray<byte>, T) wrapper body from the game assembly to see the SpanWriter instantiation
ssh root@31.97.125.237 "CID=\$(docker ps -q --filter name=dotnet-build); docker exec \$CID sh -c 'grep -n -B2 -A40 \"Write_Public_Static_Int32_ISerializer_1_T_Il2CppStructArray_1_Byte_T_0<T>.Pointer, (System.IntPtr)0\" /tmp/ssc_full.cs | head -60'"
