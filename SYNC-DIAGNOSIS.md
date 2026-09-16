# Trailer-Sync-Diagnose (Build 346 — Analyse, KEINE Code-Änderungen)

> **Wichtig / Überschneidung:** Die Parallel-Session im Worktree
> `/opt/data/slipstream-tsync` (Branch `feature/trailer-multicontainer-sync`)
> arbeitet an genau dieser Logik (per-trailer smoothing, `trailerExtraTargets`,
> Build 336 "per-trailer smoothing — alle Multi-Container gegen
> trailerExtraTargets smoothen"). Um konkurrierende Änderungen zu vermeiden,
> wurden die hier prototypisch implementierten Snapshot-Buffer-Änderungen
> **wieder vollständig reverted** — der Interpolations-Code in `Client/Client.cs`
> ist auf diesem Branch byte-identisch zu `main`. Diese Datei dokumentiert nur
> die Diagnose + einen konkreten, nicht implementierten Verbesserungsvorschlag.

## Befund: Glitch-Quellen in der aktuellen Trailer-Interpolation

Ort: `Client/Client.cs` → `SmoothTrailerMovement()` / `SmoothSingleTrailer()`
(DirectUpdate-Pfad, `TrailerSmoothTime = 0.1s`, `TrailerVelocityPreview = 0.25s`),
Targets aus `trailerExtraTargets` (multi-container, Build 336) bzw.
`trailerTargetPos` (Legacy-Single-Trailer).

1. **Kein Snapshot-Buffer (nur das neueste Target).** Pro Trailer existiert genau
   EIN Ziel (`movementTrans et` / `trailerTargetPos`). Bei Unreliable-Zustellung
   (~100 ms Periode, Server-Relay in `dedicated/MessageHandler.cs:198` leitet
   unverändert weiter) kommen Pakete gebündelt oder lückenhaft an. Der
   SmoothDamp-Sollwert springt dadurch pro eintreffendem Paket — das ist die
   Hauptursache für Clitchen/Hakeln. Ein 2–3-Snapshot-History mit
   Empfangszeitstempeln und Renderzeit = Empfangszeit + ~eine Paketperiode
   (zeitstempelbasiertes Lerp zwischen zwei Snapshots) würde den Jitter glätten.

2. **Velocity-Extrapolation ohne Correction-Fenster.**
   `SmoothSingleTrailer` addiert `rp.truckTrans.Vel * min(dt, 0.25s)` auf das
   Ziel, BEVOR der Fehler berechnet wird. Trifft dann das echte Paket ein,
   springt der Sollwert zurück (Extrapolation vs. Realität) → sichtbarer Ruck.
   Besser: Extrapolation nur als Vorhalt auf den ZEITSTEMPEL-lerpeden Wert
   zwischen zwei Snapshots, plus begrenztes Correction-Fenster (max. Aufhol-
   geschwindigkeit in m/s) statt Sollwert-Sprüngen.

3. **Erste-Paket-Verhalten nach Spawn.** Ein neu gespawnter Remote-Trailer wird
   auf das erste Paket positioniert und danach mit SmoothDamp verfolgt; das
   zweite (leicht versetzte) Paket erzeugt dann einen initialen Sprung. Mit Snapshot-
   History und `TrailerInterpDelay` (Render hinter der Empfangskante) verschwindet
   dieser Effekt.

4. **Floating-Origin-Reanchor betrifft Trailer nur indirekt.**
   `ReanchorRemotePlayersToFloatingOrigin()` (Client.cs) snapt `p.Trailer` hart
   auf `p.trailerTrans.Pos - origin`, wenn der Truck gereanchort wird — das ist
   korrekt, aber ein Origin-Wechsel MITTELS zwischen zwei Movement-Paketen
   verschiebt alle `trailerExtraTargets` (sie sind ABS-Werte) relativ zur Szene.
   Kurzzeitig entsteht so ein Fehler, den SmoothDamp dann mühsam aufholt
   (sichtbar als Trailer, der dem Truck "hinterherzieht"). Ein Origin-Wechsel
   sollte deshalb ALLE Targets (`trailerTargetPos`, `trailerExtraTargets`,
   Snapshots) um das gleiche Delta verschieben — das tut der aktuelle Code nur
   für `p.Trailer` selbst, nicht für die Targets.

## Empfohlener Fix (für die Sync-Session, NICHT hier implementiert)

- Pro Trailer (Key = `trackingId`, Legacy = `-1`) eine 3er-Snapshot-Liste
  `{time, posAbs, rot}` anfügen (bereits prototypisch gebaut und wieder
  entfernt: `AddTrailerSnapshot` / `TryInterpTrailerSnapshot` in
  `SmoothSingleTrailer`-Umbau, siehe Commit-Historie dieses Branches falls
  wiederbelebt).
- `TrailerInterpDelay ≈ 0.10 s` (eine Paketperiode), `TrailerSnapshotTTL = 0.5 s`,
  Correction-Fenster ≈ 4 m/s, hartes Snap nur bei Teleport
  (`> TruckSnapThreshold = 250 m`) oder sustained drift (Build-320-Mechanik
  beibehalten).
- Legacy-Pfad (SmoothDamp) als Fallback behalten, solange `Count < 2`
  (direkt nach Spawn gibt es nur einen Snapshot).
- Protokoll: KEINE Änderung nötig — v1 multi-trailer Format
  (`createMultiTrailerMovementMessage`, Messages.cs ~453) und Server-Relay
  bleiben unberührt; alles ist client-seitige Renderlogik.
