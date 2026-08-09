# StarTruckMP – Custom Build Changes (custom-build-11)

Diese Datei dokumentiert alle Änderungen gegenüber dem Original-Repo
(https://github.com/JayJay34/StarTruckerMP), Stand custom-build-11.
Die Quelldateien in diesem Repo sind der **unveränderte Original-Stand**
(als sichere Referenz); die Patches unten wurden darauf angewendet, um
die fertige `StarTruckMP.dll` (im Ordner `builds/`) zu bauen. Die DLL ist
geprüft identisch mit der aktuell laufenden Version (md5 56a8eb16fa1a758e81750e497a460d2e).

## Plugin.cs
- `customBuildNumber` Konstante hinzugefügt, wird im Load-Log ausgegeben:
  `Plugin StarTruckMP is loaded! [custom-build-11]`

## Server/Server.cs
- **Kritischer Fix:** `server.ClientConnected/-Disconnected/-MessageReceived`
  Subscriptions wurden VOR dem `StarTruckClient.ConnectToServer("127.0.0.1:7777")`
  Aufruf verschoben (der Call warf immer eine Exception und brach die Methode
  vorher ab -> Server verarbeitete nie Client-Nachrichten).
- `Server_ClientConnected` broadcastet jetzt `playerConnected` an alle
  bereits verbundenen Clients.
- Periodisches Logging aller Spielerpositionen alle 60s
  (`LogPlayerPositionsPeriodically`).
- Log-Zeile "Client Connected" zeigt jetzt die Client-ID.

## Client/Client.cs
- `ConnectToServer` mit try/catch und granularem Logging pro Schritt
  (myPlayer, playerCam, myTruck, floatingOrigin, spaceSuitObj), inkl.
  Fallback `GetComponentInChildren<MeshRenderer>()` falls MeshRenderer nicht
  direkt am SpaceSuit-Objekt hängt (Spielversion hat sich seit Nov 2024
  geändert).
- `ReanchorRemotePlayersToFloatingOrigin()` läuft jedes Frame und
  rekalkuliert die Position aller Remote-Spieler relativ zum aktuellen
  Floating-Origin (behebt "springt weg" Bug beim Annähern).
- `clientJoin` Handler speichert jetzt tatsächlich `pPos`/`pRot` aus der
  Roster-Nachricht in `playerInfo.truckTrans`/`playerTrans` (vorher verworfen
  -> neue Spieler sahen bereits verbundene immer bei (0,0,0)).
- `movementUpdate` Handler prüft jetzt `TryGetValue` Erfolg, bevor der
  Dictionary-Eintrag überschrieben wird.
- `SendMovement()`: erzwingt einen initialen Positions-Send auch ohne
  Bewegung (`sentFirstUpdate` Flag), behebt "Server muss sich erst bewegen"
  Bug.
- `RemoveFromSector`: Diagnose-Logging, und Bugfix wo `Rot` fälschlich mit
  `Pos` doppelt übergeben wurde.
- Periodisches Logging (60s) aller getrackten Remote-Spieler.

## Encoding/Utilities.cs
- `playerConnected` zum `messageType` enum hinzugefügt.

## Encoding/Messages.cs
- `createPlayer` komplett defensiv gemacht: try/catch, Checkpoint-Logging
  (1-9), null-Checks für "Exterior"-GameObject, spaceSuitObj, MeshRenderer
  etc. – bricht nicht mehr silent ab, sondern loggt genau wo es scheitert.
  Cosmetic-Tweaks (Hatch/Marker/Cameras/etc.) über neue Helper
  `TryDisable`/`TryDestroyComponent<T>`/`FindPath`, die fehlende Kindobjekte
  überspringen statt zu crashen.
- Neu gespawnte Truck/Player-Objekte bekommen jetzt sofort die korrekte
  `position`/`rotation` gesetzt (vorher blieben sie bei Weltursprung (0,0,0)).

## Bekannte offene Punkte (nicht in dieser DLL)
- Anhänger/Trailer werden noch nicht zwischen Spielern synchronisiert
  (siehe Analyse: `CargoContainer`/`MaglockHitchPoint` im Spiel-Assembly).
- Ob "Server muss sich bewegen" durch build-11 vollständig behoben ist,
  war beim letzten Test noch nicht final bestätigt.
