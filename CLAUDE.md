# StarTruckMP / Slipstream — agent instructions

BepInEx IL2CPP client mod + dedicated server for Star Trucker (Unity 2021.3.44f1,
Universal Render Pipeline). This file is the entry point for any coding agent
working in this repo — read it before touching code, especially before
re-diagnosing something from scratch.

## Before writing ANY reflection code or guessing a native type/field name

**Grep `reference/api-dump/*.txt` first.** It's a name-only dump (type,
properties, methods — no parameter/return types) generated from the
BepInEx/Il2CppInterop-generated interop assemblies, covering
`Assembly-CSharp`, `Assembly-CSharp-firstpass`, `Unity.TextMeshPro`,
`UnityEngine.UI`, `UnityEngine.CoreModule`, and both URP runtime assemblies.

    grep -n -A3 "^### TypeYouSuspect\b" reference/api-dump/Assembly-CSharp.txt
    grep -in "keyword" reference/api-dump/*.txt

Confirm exact signatures via runtime reflection (`type.GetProperty("X").PropertyType`)
once you've found a candidate — the dump only proves a name exists, not its type.
See `reference/api-dump/README.md` for regeneration instructions if the
interop folder changes (Unity/game version bump).

**Then read `reference/NOTES_WORLDSPACE_UI.md`** if the task involves any
in-world 3D UI, sign, billboard, or text panel. It documents concrete
gotchas already hit and fixed (or ruled out) while building
`WarpGateBillboard` across ~15 build iterations:

- The game runs URP — shader/material mismatches are a real failure mode.
- Cloning an existing TMP object as a template (the `DockingBayHUD`/
  `WarpGateBillboard` pattern, needed because `AddComponent<TextMeshProUGUI>`
  is unreliable in IL2CPP) silently carries over `enableAutoSizing`, stale
  font/material refs, and active/alpha state — all must be re-asserted
  explicitly after `Instantiate()`, or the clone exists but renders invisible
  or with your `fontSize` silently ignored.
- "Invisible from the side" is not a platform limitation — it means the
  camera-facing rotation was removed (or never added, for a design that
  actually wants a *static* sign — see below, these are different goals with
  different fixes, don't conflate them).
- `AIVehicleDriver` is a `ScriptableObject`, no Transform — dead end for
  locating NPC trucks in the scene.
- Three concrete implementation paths are written up with tradeoffs: (1)
  from-scratch static Canvas+TMP sign (fixed orientation set once, no
  per-frame rotation — matches a real airport/highway sign), (2) reusing the
  native `SpeedTrap`/`CargoBay.bayIdSign` sign objects, (3) reusing the
  native `SectorBillboard` roadside-ad system (poster-material based, tied
  to the quest/save system — needs runtime verification it exists near warp
  gates before committing to it). Read the full writeup before picking one.

## Build & release process

There is no CI — releasing IS pushing to `main` on this repo. The in-game
"Slipstream" updater reads `version.json` (via the GitHub API) to decide
whether to download a new build, and downloads from the `url` it points to
(a `raw.githubusercontent.com` link into `builds/`). **A build is not live
until all of these are done and pushed:**

1. Bump `Plugin.cs` → `customBuildNumber` (cosmetic display string, but
   must be bumped or the in-game overlay shows the previous number even
   though new code is running — this alone isn't a functional bug, just
   confusing when verifying a fix landed).
2. Build: `docker exec starttruckmp-dotnet-build-1 bash -lc "cd /src && dotnet build StarTruckMP.csproj -c Release"`
   (a long-running `dotnet-build` container with the SDK is already up via
   `docker-compose.yml` — don't try to install dotnet locally).
3. `gzip -c bin/Release/net6.0/StarTruckMP.dll > builds/StarTruckMP-custom-build-N.dll.gz`
4. Update `version.json` — both `build` and `url` fields, matching N exactly.
5. `git add` the source changes AND the new `builds/*.dll.gz` AND
   `version.json` together, commit, `git push origin HEAD`. Missing the
   `builds/` artifact or `version.json` in the commit is a silent failure
   mode — the source lands on GitHub but the updater keeps serving the old
   build with no error. Verify after pushing:
   `curl -s https://raw.githubusercontent.com/yelli77/slipstream/main/version.json`
   should show your new build number, and
   `curl -sI https://raw.githubusercontent.com/yelli77/slipstream/main/builds/StarTruckMP-custom-build-N.dll.gz`
   should be `HTTP/2 200`.

## Current open task: jumpgate departure board

Airport-style display board next to each warp gate showing who has it set as
their destination (`POS 1. PlayerName --- distance`, ranked by distance).
Text should NOT rotate to face the camera (that was the wrong approach on an
earlier attempt) — it's meant to look like a fixed, static sign you read as
you fly past, like a real airport gate display. See
`reference/NOTES_WORLDSPACE_UI.md` for the three implementation options and
their tradeoffs before starting.
