#!/bin/bash
# Prepend 332 section to CHANGES.md on VPS tree, commit on feature branch, push, merge to main and push main
set -e
ssh root@31.97.125.237 'cd /docker/starttruckmp/src && git checkout CHANGES.md && python3 - << "PYEOF"
header = """## Neu in custom-build-332: SystemSaveData-Discriminator NATIV schreiben (Offset-Fix) + natives Union-Gate

- Root Cause (331): Der Interop-Wrapper-Write (_Discriminator_k__BackingField / value) schrieb
  zwar sichtbar ins Objekt (Managed-Readback disc=12), aber der NATIVE Serializer
  (SaveSlotContainer.GeneratedSerializer.GetMaxSizeOf(SystemSaveData)) las weiterhin
  Discriminator=0 -> "Exception determining type of union" blieb.
- FIX 332: NATIVER Write + Readback ueber echte FieldInfo-Pointer (il2cpp_class_get_field_from_name
  auf der nativen SystemSaveData-Klasse, il2cpp_field_set_value fuer den byte-Discriminator,
  il2cpp_gc_wbarrier_set_field am nativen value-Offset) statt der generierten Wrapper-Properties.
- GATE: Nach dem Write laeuft die NATIVE per-type-Funktion
  SaveSlotContainer.GeneratedSerializer.GetMaxSizeOf_0f0220f020f24c68a1c77969f8d98136(SystemSaveData)
  direkt (public static im Interop-DLL). Fliegt dort keine Union-Exception, hat der native
  Serializer den Discriminator gesehen. Diagnose: "natives Union-Gate bestanden: GetMaxSizeOf=...".
- Release: builds/StarTruckMP-custom-build-332.dll.gz, version.json -> custom-build-332 (Feed live).

"""
with open("CHANGES.md") as f: content = f.read()
with open("CHANGES.md","w") as f: f.write(header + content)
print("CHANGES updated")
PYEOF
git add CHANGES.md && git commit -q -m "docs: CHANGES.md fuer custom-build-332" && git push -q origin feature/systemsavedata-native-write-332 && git checkout -q main && git merge -q --ff-only feature/systemsavedata-native-write-332 2>/dev/null || git merge -q --no-edit feature/systemsavedata-native-write-332 && git push -q origin main && echo MAIN_PUSHED && git log --oneline -3'
