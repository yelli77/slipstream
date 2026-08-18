# Notes: building 3D/world-space signs & billboards in this project (IL2CPP + URP)

Written after debugging `WarpGateBillboard` across builds 218-233. Read this
BEFORE re-attempting a world-space text panel/sign, so the same ground isn't
re-covered from scratch.

## The project uses URP

`Unity.RenderPipelines.Universal.Runtime.dll` is present in
`bepinex/interop/` — confirmed Universal Render Pipeline, not Built-in.
Anything that assigns a material/shader by name (custom cubes, custom quads)
needs a URP-compatible shader, not the Built-in "Standard"/legacy shaders. If
a mesh renders solid black, magenta, or invisible, check the shader/pipeline
match first.

## "It's invisible from the side" is not a fundamental limitation

A flat quad/canvas plane IS only visible from the front — that's normal 3D
geometry, not an IL2CPP or URP problem. The fix is a **billboard behavior**:
rotate the object to face the camera every frame
(`transform.rotation = Quaternion.LookRotation(camera.transform.position - transform.position, Vector3.up)`
in an `Update()`). This is plain Unity math, works fine under IL2CPP, and is
how every "billboard" (health bar, name tag, sign) in every game is done.
Builds 226-227 removed this rotation ("static, no camera tracking") and then
correctly observed the panel goes invisible edge-on — that's the removal
causing the symptom, not a platform limitation. Don't drop the rotation;
if it was flickering, debug *why* (likely floating-origin rebase interacting
with `Camera.main` caching — see below), don't remove the mechanism.

## Cloning an existing TMP object as a "template" (the DockingBayHUD pattern)

`AddComponent<TextMeshProUGUI>()` from scratch in IL2CPP often ends up with a
missing font asset/material, so the common workaround is to
`Instantiate()` an existing in-scene `TextMeshProUGUI` GameObject and
reconfigure it. This works, but the clone silently carries over state that
can make it invisible even though the GameObject exists:

- `enableAutoSizing` can survive the clone and silently recompute `fontSize`
  down to near-zero in a differently-proportioned RectTransform — it **must**
  be explicitly set to `false` before setting your own `fontSize`, or your
  `fontSize` assignment is silently overridden.
- Re-bind `.font` and `.fontSharedMaterial` explicitly from the source
  after cloning rather than trusting the cloned reference.
- Re-assert `gameObject.SetActive(true)`, `CanvasRenderer.SetAlpha(1f)`,
  and any `CanvasGroup.alpha = 1f` after cloning — Instantiate() preserves
  whatever active/alpha state the source object happened to have.
- Call `Canvas.ForceUpdateCanvases()` after building the object instead of
  waiting an indeterminate number of frames for IL2CPP's update loop.

See `Client/WarpGateBillboard.cs` git history around build 220
(`git show 905ab0b`) for the concrete fix once this bit.

## fontSize tuning is manual once autosizing is off

Once `enableAutoSizing = false`, the `fontSize` value is taken literally —
there's no reference frame ("fits the box") like autosizing gives you. For a
canvas sized e.g. 2000x3000 units, single-line labels need `fontSize` in the
low hundreds (not tens) to actually read as legible text. Tune by eye against
the RectTransform's `sizeDelta`, not by guessing small numbers.

## AIVehicleDriver has no Transform

`AIVehicleDriver : ScriptableObject` (see `reference/api-dump/Assembly-CSharp.txt`)
— it's a driver *data record*, not attached to any GameObject in the scene.
It cannot be used to find an NPC truck's world position/velocity. Whatever
in-scene MonoBehaviour actually represents a spawned NPC truck hasn't been
identified yet — check `reference/api-dump/Assembly-CSharp.txt` for
`FakeNPCTruckInDockingBay` and similar names before trying `AIVehicleDriver`
again for positional data.

## Before writing new reflection code: grep the dump first

`reference/api-dump/*.txt` has every type/property/method name from
Assembly-CSharp, Assembly-CSharp-firstpass, Unity.TextMeshPro, UnityEngine.UI,
UnityEngine.CoreModule, and both URP runtime assemblies. Grep it for a
keyword before writing `GetField`/`GetProperty` reflection against a guessed
name — see `reference/api-dump/README.md` for usage and its limitation (names
only, no parameter/return types).

## Recipe: static "airport departure board" style sign (fixed orientation, no rotation)

If the goal is a real airport/highway-sign look — a board mounted next to the
gate that does **not** rotate to face the camera, and you read it as you fly
past — the camera-facing "billboard" trick above is the WRONG tool. Don't add
`Update()` rotation at all for this case. Instead:

1. **Position**: offset to the side of the gate's approach corridor and up,
   not on the direct flight line (see build 221,
   `git show 51a97e7 -- Client/WarpGateBillboard.cs` — `SideOffset`/
   `HeightOffset` computed from `Vector3.Cross(Vector3.up, approachDirection)`).
   This is still correct and independent of the rotation question.

2. **Orientation: set ONCE at creation, never touched again.** Don't compute
   it from `Camera.main` (that's what makes it a rotating billboard). Compute
   it from the gate/corridor geometry instead, e.g.:
   ```csharp
   // Face the sign back along the approach direction, tilted very slightly
   // toward where trucks are expected to fly past, like a real roadside sign
   // angled to be readable from the direction of travel.
   Quaternion fixedRot = Quaternion.LookRotation(-approachDirection, Vector3.up);
   root.transform.rotation = fixedRot;
   ```
   No `BillboardBehavior`/`Update()` component at all — the object is 100%
   static after `CreateBillboard()` runs. This also sidesteps any flicker
   concerns from per-frame rotation interacting with floating-origin rebasing
   (root.transform.SetParent(null) means its transform is in absolute world
   space, so it needs no per-frame repositioning either — position it once).

3. **Single flat plane is fine here** — you don't need the "cross-billboard
   2 perpendicular planes" workaround from build 232. That trick exists to
   make a sign readable from *any* angle when it can't rotate; a real airport
   board is only meant to be read from the approach direction, same as here.
   One plane, correctly oriented once, matches the reference photo (a single
   flat road sign) and is simpler/more robust than the dual-plane hack.

4. Everything from the TMP-cloning section above (autosizing off, font/material
   rebind, active-state/alpha reset, `Canvas.ForceUpdateCanvases()`, and
   fontSize tuned in the low hundreds) still applies — that part was never the
   rotation-related issue.
