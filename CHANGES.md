## Neu in custom-build-215: DockingBay Name Format

- DockingBayHUD Name-Anzeige geaendert: "Bay {name} - Jobs" -> "{name} (Jobs)".
## Neu in custom-build-210: DockingBayHUD Font + Position Fix

- Name-Label groesser (fontSize 12->14) und DIST-Label (11->13) fuer bessere Lesbarkeit.
- Font-Fallback: Prueft ob nameLabel.font nach IL2CPP-Instantiate null ist und setzt explizit.
- ForceMeshUpdate() nach Text-Setzung erzwingt sofortiges Rendering.
- Marker-Position: Nutzt Renderer.bounds.center des DockingBay-Kind-Objekts statt
  der DockingBay-Transform-Position fuer genauere visuelle Platzierung am Docking-Pad.


## Neu in custom-build-208: WarpGateHUD — Stargate-Nachbaranzeige

- An jedem WarpGate (Stargate) im Sektor wird ein ScreenSpace-Overlay angezeigt,
  welcher Spieler im 2km-Umkreis als naechster springen darf.
- Zeigt: Gate-Name, Spielername + Entfernung (m/km),
  "Kein Spieler in Reichweite" wenn niemand in 2km.
- Auto-Hide bei >5km Kamera-Entfernung, Orange-Dimming fuer Off-Screen Gates.
- Aktualisiert sich alle 2s (Gate-Refresh) bzw. 0.2s (Text-Updates).

# StarTruckMP — Custom Build Changes (custom-build-15)

## Server-Update (kein neuer Client-Build noetig): Rueckkehr zu General beim Disconnect

- Wenn ein Spieler das Spiel beendet oder die Verbindung zum StarTruckMP-Server
  verliert, wird er in Discord automatisch aus seinem Sektor-Voice-Channel
  zurueck in den General-Channel verschoben (falls verknuepft und aktuell in
  einem Voice-Channel).
- Discord-Bridge: neuer Endpunkt /player-disconnect, loest den Spieler auch
  aus dem Online-Tracking (fuer /link) heraus.


## Neu in custom-build-137: Slipstream-Branding im HUD

- Sobald der Discord-Link-Code ausgeblendet wird (nach erfolgreicher
  Verknuepfung), steht an seiner Stelle jetzt "Slipstream" im HUD.


## Neu in custom-build-136: Link-Code verschwindet nach Verknuepfung, weniger Log-Spam

- Discord-Link-Code im HUD wird jetzt automatisch ausgeblendet, sobald die
  Verknuepfung erfolgreich war. Client fragt alle 8 Sekunden beim Server nach
  dem Verknuepfungsstatus (RequestLinkStatus/LinkStatus-Nachrichten), Server
  prueft das bei der Discord-Bridge (neuer Endpunkt /link-status/:steamId).
- Log-Spam entfernt: die "hitched cargo detected"-Zeile wurde jeden Frame
  geloggt, sobald ein Anhaenger dran war. Der eigentliche Statuswechsel wird
  weiterhin sauber geloggt, nur das Dauerfeuer ist weg.


## Neu in custom-build-135: Sofort-Verschieben beim Linken + Name/System im HUD

- HUD oben links zeigt jetzt dauerhaft Spielername und aktuelles System an
  (z.B. 'Yelli_ — Purity'), aktualisiert sich bei jedem Sektorwechsel.
  Solange noch nicht verknuepft, steht der Discord-Link-Code als zweite Zeile
  mit dabei.
- Beim Verknuepfen (`/link <code>` in Discord) wird man jetzt SOFORT in den
  passenden System-Voice-Channel verschoben, falls der Bot bereits einen
  aktuellen Sektor kennt und man in Discord in einem Voice-Channel ist. Vorher
  musste man erst einmal springen oder sich neu verbinden, damit die erste
  Verschiebung ausgeloest wurde.
- Discord-Bridge: Online-Spieler-Erkennung passiert jetzt sofort beim Connect
  (SteamID-Empfang), nicht erst beim ersten Sektorwechsel — verhindert, dass
  Spieler, die lange in einem Sektor bleiben, fuer `/link` unsichtbar sind.


## Neu in custom-build-134: Discord-Link-Code im HUD

- Oben links im Spiel wird nach dem Verbinden dauerhaft ein Discord-Link-Code
  angezeigt (letzte 6 Ziffern der SteamID). Diesen Code gibst du in Discord bei
  `/link <code>` ein, um deinen Discord-Account eindeutig mit deinem
  In-Game-Charakter zu verknuepfen — kein Rateflug mehr bei mehreren Online-
  Spielern.
- Discord-Bot: `/link` fragt jetzt nach dem Code statt eine Auswahlliste aller
  online sichtbaren Spieler zu zeigen (verhindert versehentliches/falsches
  Verknuepfen mit dem falschen Spieler).
- Bugfix: SteamID wurde beim Uebertragen an die Discord-Bridge als JSON-Zahl
  gesendet und dabei durch JavaScripts Zahlenpraezision (max. sicher darstellbar
  bis 2^53) fehlerhaft gerundet — SteamIDs sind 64-Bit-Werte und ueberschreiten
  das deutlich. Wird jetzt als String uebertragen, keine Praezisionsverluste mehr.


## Neu in custom-build-133: SteamID-Erfassung + Discord-Bridge-Hooks

- Client sendet beim Connect die eigene SteamID (per Reflection auf
  `Steamworks.SteamUser.GetSteamID()`, da CSteamID ein IL2CPP-Interop-Struct
  ist) an den Server. Bei Fehler (z.B. Steam nicht initialisiert) wird
  SteamId=0 gesendet und als "nicht identifiziert" behandelt.
- Neuer Nachrichtentyp `setPlayerSteamId`, neues Feld `PlayerState.SteamId`.
- Dedicated Server: Bei jedem Sektorwechsel (`HandleSector`) und bei
  `!link <code>`-Chatbefehlen wird ein Fire-and-Forget-HTTP-POST an einen
  künftigen Discord-Bridge-Bot geschickt (`STARTTRUCKMP_BRIDGE_URL`, Default
  `http://localhost:4500`) — Vorbereitung für automatisches Verschieben von
  Spielern zwischen Discord-Voice-Channels je nach Sektor. Der Bot-Service
  selbst existiert noch nicht, POSTs laufen aktuell ins Leere (erwartet,
  fehlerfrei toleriert, rate-limitiertes Warn-Logging max. 1x/30s).
- JSON für die Bridge-POSTs wird über `System.Text.Json.JsonSerializer`
  gebaut (nicht mehr manuelle String-Concatenation) — vermeidet kaputtes
  JSON bei Sonderzeichen (z.B. " im `!link`-Code).


Diese Datei dokumentiert alle Änderungen gegenüber dem Original-Repo
(https://github.com/JayJay34/StarTruckerMP), Stand custom-build-12.
Die Quelldateien in diesem Repo enthalten die tatsächlich gepatchten Stände
(inkl. aller Änderungen aus custom-build-11 und dem neuen Anhänger-Sync aus
custom-build-12). Die DLL (im Ordner `builds/`) ist geprüft identisch mit der
aktuell laufenden Version (md5 881d81eb45a8ca1ed2fd5039d395972a).


## Neu in custom-build-15: Echtes Anhänger-Mesh statt Placeholder-Würfel

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
- **Bugfix Floating Origin:** `SendTrailerMovement()` sendet jetzt die
  korrekte Welt-Position (`rb.position + floatingOrigin`) statt der
  reinen Scene-Local-Position. Vorher war der Anhänger um
  `-2×floatingOrigin` verschoben (zehntausende Einheiten entfernt,
  unsichtbar).
- **Performance:** `ReanchorRemotePlayersToFloatingOrigin()` hat jetzt
  einen Dirty-Check — überspringt das Reanchoring wenn sich das
  Floating-Origin seit dem letzten Tick nicht geändert hat. Reduziert
  unnötige Transform-Schreibvorgänge um ~99%.

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
