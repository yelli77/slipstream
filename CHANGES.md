# StarTruckMP — Custom Build Changes (custom-build-14)

Diese Datei dokumentiert alle Änderungen gegenüber dem Original-Repo
(https://github.com/JayJay34/StarTruckerMP), Stand custom-build-12.
Die Quelldateien in diesem Repo enthalten die tatsächlich gepatchten Stände
(inkl. aller Änderungen aus custom-build-11 und dem neuen Anhänger-Sync aus
custom-build-12). Die DLL (im Ordner `builds/`) ist geprüft identisch mit der
aktuell laufenden Version (md5 881d81eb45a8ca1ed2fd5039d395972a).


## Neu in custom-build-14: Echtes Anhänger-Mesh statt Placeholder-Würfel

- `Messages.createTrailerMesh()`: Neue Methode, die das lokale
  `CargoContainer`-GameObjekt (via `MaglockHitchPoint.cargo`) instantiiert
  und als Remote-Anhänger nutzt. Das echte Mesh mit allen Materialien wird
  geklont, Game-Logic-Komponenten (`CargoContainer`, `Rigidbody`,
  `MaglockHitchPoint`) werden zerstört, alle `Collider` deaktiviert.
- Fallback: Wenn kein lokaler Anhänger gehitcht ist, oder bei einem Fehler,
  wird weiterhin der blaue Placeholder-Würfel verwendet.
- `Client/Client.cs`: `trailerMovementUpdate`-Handler ruft jetzt
  `createTrailerMesh()` statt `createTrailerPlaceholder()` auf.
- `Server/Server.cs`: Beim `clientJoin` (neuer Client verbindet sich)
  sendet der Server jetzt für jeden existierenden Spieler mit gehitchtem
  Anhänger ein `trailerMovementUpdate` an den neuen Client — damit auch
  Anhänger, die schon bei Spielstart dran sind, korrekt gespawnt werden.

## Neu in custom-build-12: Anhänger/Trailer-Synchronisation

- Neuer Nachrichtentyp `trailerMovementUpdate` (Encoding/Utilities.cs) sowie
  neue Felder in `playerInfo`: `Trailer` (GameObject), `trailerTrans`,
  `trailerHitched`.
- Client/Client.cs: `SendTrailerMovement()` prüft per
  `MaglockHitchPoint`/`CargoContainer` (Spiel-Assembly), ob am eigenen Truck
  gerade ein Anhänger angekuppelt ist, und sendet Kuppel-Status +
  Position/Rotation an den Server. Wird aus der bestehenden `SendMovement()`-
  Schleife heraus aufgerufen.
- Server/Server.cs: neuer `case` in `Server_MessageReceived` für
  `trailerMovementUpdate` — übernimmt den Zustand in `playerList` und
  broadcastet ihn an alle anderen Clients (`SendToAll`).
- Client.cs (Empfang): beim Empfang von `trailerMovementUpdate` wird für den
  jeweiligen Remote-Spieler bei Bedarf ein Platzhalter-Objekt gespawnt
  (`Messages.createTrailerPlaceholder`) bzw. beim Abkuppeln wieder zerstört.
  Position/Rotation werden laufend über `Messages.updateMovement` aktualisiert
  und in `ReanchorRemotePlayersToFloatingOrigin()` zusammen mit Truck/Player
  gegen den Floating Origin reanchored.
- Messages.cs: `createTrailerPlaceholder()` erzeugt bewusst **keinen** Klon
  des echten Anhänger-Modells, sondern einen einfachen blauen Würfel
  (Collider deaktiviert, nur Sichtreferenz). Grund: anders als beim
  Truck/Player-Exterior gibt es keine Garantie, dass ein empfangender Client
  ein passendes lokales Anhänger-Prefab zum Klonen bereithält (verschiedene
  Anhängertypen im Spiel). Der Platzhalter ist bewusst als einfache,
  risikoarme Lösung gewählt und **noch nicht live im Spiel getestet**.
- Aufräumen: Anhänger-Platzhalter werden korrekt zerstört bei
  Client-Disconnect, `clientDisconnect`-Nachricht und beim Sektorwechsel
  (`RemoveFromSector`).

## custom-build-11 (vorherige Änderungen, weiterhin enthalten)

### Plugin.cs
- `customBuildNumber` Konstante (jetzt "custom-build-12"), wird im Load-Log
  ausgegeben: `Plugin StarTruckMP is loaded! [custom-build-12]`

### Server/Server.cs
- **Kritischer Fix:** `server.ClientConnected/-Disconnected/-MessageReceived`
  Subscriptions wurden VOR dem `StarTruckClient.ConnectToServer("127.0.0.1:7777")`
  Aufruf verschoben (der Call warf immer eine Exception und brach die Methode
  vorher ab -> Server verarbeitete nie Client-Nachrichten).
- `Server_ClientConnected` broadcastet jetzt `playerConnected` an alle
  bereits verbundenen Clients.
- Periodisches Logging aller Spielerpositionen alle 60s
  (`LogPlayerPositionsPeriodically`).
- Log-Zeile "Client Connected" zeigt jetzt die Client-ID.

### Client/Client.cs
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

### Encoding/Utilities.cs
- `playerConnected` zum `messageType` enum hinzugefügt.

### Encoding/Messages.cs
- `createPlayer` komplett defensiv gemacht: try/catch, Checkpoint-Logging
  (1-9), null-Checks für "Exterior"-GameObject, spaceSuitObj, MeshRenderer
  etc. — bricht nicht mehr silent ab, sondern loggt genau wo es scheitert.
  Cosmetic-Tweaks (Hatch/Marker/Cameras/etc.) über neue Helper
  `TryDisable`/`TryDestroyComponent<T>`/`FindPath`, die fehlende
  Kindobjekte überspringen statt zu crashen.
- Neu gespawnte Truck/Player-Objekte bekommen jetzt sofort die korrekte
  `position`/`rotation` gesetzt (vorher blieben sie bei Weltursprung (0,0,0)).

## Bekannte offene Punkte
- Der Anhänger-Sync (custom-build-12) ist neu implementiert, aber noch nicht
  mit mehreren Spielern live im Spiel getestet — der Platzhalter-Würfel
  könnte optisch nicht überzeugen, auch wenn die Netzwerklogik funktionieren
  sollte.
- Ob "Server muss sich zuerst bewegen" durch build-11 vollständig behoben
  ist, war beim letzten Test noch nicht final bestätigt.
