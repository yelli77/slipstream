#!/bin/bash
# Add 332 CHANGES.md section on top of 2fbafcf, commit, push main (final docs)
set -e
ssh root@31.97.125.237 'cd /docker/starttruckmp/src && python3 - << "PYEOF"
header = """## Neu in custom-build-332: SystemSaveData-Discriminator NATIV schreiben (Offset-Fix) + natives Union-Gate

- Root Cause (331): Der Interop-Wrapper-Write (_Discriminator_k__BackingField / value) schrieb
  sichtbar ins Objekt (Managed-Readback disc=12), aber der NATIVE Serializer
  (SaveSlotContainer.GeneratedSerializer.GetMaxSizeOf(SystemSaveData)) las weiterhin
  Discriminator=0 -> "Exception determining type of union" blieb.
- FIX 332: NATIVER Write + Readback ueber echte FieldInfo-Pointer (IL2CPP.GetIl2CppField auf der
  nativen SystemSaveData-Klasse): il2cpp_field_set_value fuer den byte-Discriminator,
  il2cpp_gc_wbarrier_set_field am nativen value-Offset, nativer Readback via il2cpp_field_get_value.
- GATE: Nach dem Write laeuft die NATIVE per-type-Funktion
  SaveSlotContainer.GeneratedSerializer.GetMaxSizeOf_0f0220f020f24c68a1c77969f8d98136(SystemSaveData)
  (public static im Interop-DLL). Exception hier = Write wirkungslos; ohne Exception hat der
  native Serializer den Discriminator gesehen. Diagnose-Log: "natives Union-Gate bestanden".
- Test: STRUCKMP_ROUNDTRIP-Trigger wie bei 330/331 -> Log-Zeile "roundtrip kind match: X/X".
- Release: builds/StarTruckMP-custom-build-332.dll.gz, version.json -> custom-build-332.

"""
with open("CHANGES.md") as f: content = f.read()
with open("CHANGES.md","w") as f: f.write(header + content)
PYEOF
git add CHANGES.md && git commit -q -m "docs: CHANGES.md fuer custom-build-332" && git push -q origin main && echo MAIN_PUSHED && git log --oneline -2'