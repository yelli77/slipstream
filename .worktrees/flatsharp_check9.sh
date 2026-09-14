#!/bin/bash
# Look at ISerializerExtensions.Write wrapper bodies in ssc_full.cs (game assembly interop dump)
ssh root@31.97.125.237 "grep -n 'Il2CppStructArray<byte> data' /tmp/ssc_full.cs | head -5"
