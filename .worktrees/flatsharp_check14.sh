#!/bin/bash
# Check where GetMaxSizeOf_dec9a062 (QuestSaveData) is called from - find the caller chain, and see how GeneratedSerializer.GetMaxSize(SaveSlotContainer) implements the Union dispatch (native side)
ssh root@31.97.125.237 "CID=\$(docker ps -q --filter name=dotnet-build); docker exec \$CID sh -c 'grep -n -B5 -A30 \"public unsafe virtual int GetMaxSize(SaveSlotContainer root)\" /tmp/ssc_full.cs | head -20; echo ...; grep -c . /tmp/ssc_full.cs'"
