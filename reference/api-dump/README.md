# Game API dump (Assembly-CSharp)

Generated from the BepInEx/Il2CppInterop-generated interop assemblies
(`bepinex/interop/Assembly-CSharp*.dll`) via metadata reflection (Python +
`dnfile`), not from a live game process. Regenerate any time the game/BepInEx
interop folder is updated (Unity/Il2Cpp version bump), since these files are
`.gitignore`d build output on your machine — only this dump is checked in.

## What's in here

- `Assembly-CSharp.txt` — every game-defined type with at least one public
  property or method, grouped `### Namespace.TypeName : BaseType` followed by
  its `properties:` and `methods:` (comma-separated names only).
- `Assembly-CSharp-firstpass.txt` — same, for the firstpass assembly.

## What this is for

Before reaching for ad-hoc reflection (`GetField`/`GetProperty` with guessed
names) or before assuming a field/behavior doesn't exist, grep here first:

    grep -n -A3 "TypeNameYouSuspect" reference/api-dump/Assembly-CSharp.txt
    grep -n -i "sign\|billboard\|waypoint" reference/api-dump/Assembly-CSharp.txt

This is how the `SpeedTrap` (native speed-limit sign, `m_signText` /
`m_signMaintainedSpeedColor` / `m_signExceededSpeedColor`), `CargoBay.bayIdSign`,
and `AIVehicleDriver` (turns out to be a `ScriptableObject`, no Transform —
not usable for locating NPC trucks in the scene) were found/ruled out while
debugging WarpGateBillboard.

## Known limitation

Only member **names** are dumped, not parameter/return types (that needs full
ECMA-335 method-signature blob decoding, which this quick script doesn't do).
Once grep finds a promising type/member, confirm the exact signature at
runtime via reflection (`type.GetProperty("X").PropertyType`, etc.) before
relying on it — same as you would have had to do anyway, just now you know
*where* to look instead of guessing blind.

## Still missing: live scene hierarchy

This dump only shows class *definitions* — not how objects are actually
wired up in a given scene at runtime (parenting, which prefab has which
components attached, real instance names). For that, install UnityExplorer
(BepInEx plugin) alongside the mod and inspect the live GameObject hierarchy
in-game; there's no substitute for that when placing a new UI element
relative to existing scene objects.
