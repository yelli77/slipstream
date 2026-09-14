#!/bin/bash
# Inspect SystemSaveData proxy: 'value' field wrapper type, and ValueTuple/SpanWriter usage of WriteInlineValueOf_0f0220f020f24c68a1c77969f8d98136 (SystemSaveData inline, takes byref ValueTuple)
ssh root@31.97.125.237 "CID=\$(docker ps -q --filter name=dotnet-build); docker exec \$CID sh -c 'grep -n -B2 -A10 \"public unsafe.*value$\" /tmp/ssd_proxy.cs | head -30'"
