## Neu in custom-build-388: Job-Sync (Server-Pool) + Cargo-Sync per Kill-Switch AUS

- Grund: Spiel-Freeze nach Job-Annahme im Multiplayer (Atlas Prime -> Purity, beide Spieler).
- `JobBoardServerSync.ENABLED = false` (Client/JobBoardServerSync.cs): kein Upload, kein Pool-Empfang,
  kein jobTaken, kein Board-Filter, kein Accept-Guard. Jeder Spieler nutzt seine lokalen Spiel-Jobs.
- `CargoSync` (Container-Sync, Grundlage des Job-Syncs) haengt am selben Schalter und ist ebenfalls aus.
- Server-Relay bleibt unveraendert (wird einfach nicht mehr benutzt). Wieder einschalten: ENABLED = true.

## Neu in custom-build-333: PLAN B Same-Seed lite - Job-Sync ohne IL2CPP-Serialization

- Entscheidung (Michael): die Serialize/Deserialize-Seite (FlatSharp/QuestTaskParameterSaveData-
  Union-Restore) bleibt Dead-End und wird nicht weiterverfolgt. Neuer Weg: Kennungs-Broadcast +
  lokale Filterung ("Same-Seed lite") - KEINE Job-Objekte/Save-Daten mehr ueber die Leitung.
- Neu: Client/JobBoardIdSync.cs
  - Autoritaet (niedrigste Spieler-ID im Sektor, wie JobBoardSync) broadcastet nach jeder
    Job-Generierung (Postfix GenerateJobsForAllSectors), bei Spielerankunft im Sektor und
    periodisch (2s) NUR Kennungen "(questId|displayName)" ihrer Live-Jobs
    (neue Message jobBoardIdents=18, kleine Reliable-Message, kein ChunkedBlobTransfer).
  - Empfaenger puffert die Kennungen je Sektor (60s Gueltigkeit) und FILTERT seine eigene
    GetAvailableJobs()-Liste im JobBoardComputer genau auf diese Kennungen (reine Anzeige-
    Ebene - ProceduralJobGenerator/QuestTracker werden NICHT angetastet, kein Restore-Pfad,
    kein QuestTaskParameterSaveData).
  - DIAGNOSE (Michael-Test): beim Board-Oeffnen (J) Log-Zeile "meine Jobs: [...] + empfangene
    Kennungen: [...] + match: X/Y" (BepInEx-Log) und dieselbe Zeile unten im Board-Overlay.
- Server: neues MessageType.JobBoardIdents=18 (common/MessageTypes.cs) + reiner Relay-Handler
  HandleJobBoardIdents in dedicated/MessageHandler.cs (Strings + Absender-ID, SendToAll).
  WICHTIG: ID 18 (nicht 16) - der Client-enum (Encoding/Utilities.cs) hat die Legacy-Eintraege
  setDestinationGate=16/multiTrailerMovementUpdate=17 dazwischen.
- Alte Blob-Sync-Infrastruktur (JobBoardSync v2 + CargoSync) bleibt unveraendert aktiv -
  Kennungs-Broadcast ist zusaetzlich und unabhaengig.
- Release: builds/StarTruckMP-custom-build-333.dll.gz, version.json -> custom-build-333,
  Dedicated-Server-Image rebuilt + Container neu gestartet (Relay live).

## Neu in custom-build-332: SystemSaveData-Discriminator NATIV schreiben (Offset-Fix) + natives Union-Gate

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

## Neu in custom-build-331: SystemSaveData-Union manuell aufbauen (Discriminator-Fix)

- Root Cause: Der managed Union-CTor `new SystemSaveData(questSave)` ruft zwar den nativen
  Union-CTor via runtime_invoke, das Spiel-side Union-Objekt behielt aber Discriminator=0
  (NONE). Der native FlatSharp-Pfad crashte dann in `GetMaxSizeOf(SystemSaveData)` mit
  `Exception determining type of union. Discriminator = 0`.
- Dekompiliert: SystemSaveData-Union besteht aus genau zwei Feldern: `<Discriminator>k__BackingField`
  (byte) und `value` (Il2CppSystem.Object). Die Interop-Wrapper `_Discriminator_k__BackingField`
  und `value` schreiben beide direkt per Feld-Offset (byte) bzw. `il2cpp_gc_wbarrier_set_field`
  (Referenz) — ohne runtime_invoke, also ohne den CTor-Pfad, der Discriminator nicht setzte.
- FIX: `CreateQuestSaveDataUnion()` — `new SystemSaveData()` (Default-CTor), dann
  `_Discriminator_k__BackingField = 12` (ItemKind.QuestSaveData) und `value = questSave`;
  Readback-Log + Guard-Throw, falls der Discriminator-Write nicht haengt.
- Diagnose-Zeile im Log: `JobBoardSync: SystemSaveData-Union manuell: disc=12 (erwartet 12), value=gesetzt`.

## Neu in custom-build-330: Datei-basierter Roundtrip-Trigger (Env-Var kam nicht durch)

- Root Cause: Der Slipstream-Updater startet das Spiel primaer via `steam://run/2380050`
  (UseShellExecute=true). Damit ist STEAM der Elternprozess des Spiels - Environment-Variablen
  des Updaters (auch via psi.EnvironmentVariables gesetzt) erreichen den Spiel-Prozess
  nicht zuverlaessig. Der Blackscreen-Kommentar (SteamAPI_Init ohne Steam-Session) verbietet
  den Direkt-.exe-Start als Primaerpfad, also ist keine Env-Kette sicher.
- FIX (Datei-Trigger, startart-unabhaengig): Der Roundtrip-Trigger ist jetzt ERFUELLT wenn
  ENV `STRUCKMP_ROUNDTRIP=1` ODER die Datei `<BepInEx>/config/STRUCKMP_ROUNDTRIP.txt`
  existiert (Inhalt egal). Diagnose-Log beim Plugin-Init: `roundtrip trigger: env=<wert> file=<true|false>`;
  Disabled-Zeile + Fallback-Trigger checken beide Quellen.
- Updater: env-passthrough-Diagnose-Log (`env passthrough: STRUCKMP_ROUNDTRIP=<wert|missing>`)
  beim steam://-Start - dokumentiert, was der Launcher sieht.
- Test durch Michael: `echo. > "%GAME%\BepInEx\config\STRUCKMP_ROUNDTRIP.txt"` anlegen,
  Spiel wie ueblich via Slipstream starten, Roundtrip-Zeile im Log pruefen, Datei danach
  loeschen.

## Neu in custom-build-322: Departure-Board — TMP.enabled Re-Assert beim Klonen

- Ursache eingegrenzt: Die ZWEITE Board-Generation nach Sektorwechsel klonite ein Template-TMP,
  dessen Komponente zwischen Generation 1 und 2 von irgendwas (Rebuild-Pfad, andere Mods, Culling
  im verlassenen Sektor) disabled wurde — Instantiate() kopiert den enabled-Zustand mit, der Klon
  war also geboren disabled => schwarzer Kasten (Diag-Log: alles OK ausser tmp.enabled=False).
- Fix (a): tmp.enabled=true FORCIERT direkt nach jedem Klon, symmetrisch zu alpha/canvas.
- Fix (b): Template-Kandidaten mit disabled TMP werden nicht geklont (wie font-loses Template).
- Fix (c): Eigene Board-TMPs (DepartureText / root DepartureBoard_*) werden nie als Template
  benutzt — Boards koennen sich nicht mehr selbst als Quelle klonen.

## Neu in custom-build-308: Cleanup

- Diagnose-Dumps entfernt (Cockpit-Experimente 300-303), Jobboard-Funktion unveraendert.

## Neu in custom-build-307: Jobboard-Pfad

- Original-Jobboard via MenuState.LoadAndShow (Primaerpfad), DevPanel-Fallback, Overlay letzte Notloesung.

## Neu in custom-build-293: Cockpit-Fix

- Cockpit-Panel klont jetzt ein Spiel-TMP (Font/Material) statt frisches TMP ohne Font (-rendered nothing-).
- Log zeigt fontClone=true/false fuer Diagnose.

## Neu in custom-build-292: Cockpit-Display-Integration

- J-Toggle zeigt das Jobboard jetzt bevorzugt auf dem echten LLAMA-Cockpit-Display (World-Space-TMP unter MonitorOverlaySwitcher.popupsRootTransform, von der Monitor-Kamera gerendert).
- Fallback: wenn Cockpit-Panel nicht gefunden wird, bleibt das Screen-Overlay aktiv.
- Test-Log: Cockpit-Display an/aus vs Overlay an (Fallback).

## Neu in custom-build-291: Boardcomputer-Politur

- Liste auf 10 Jobs begrenzt + "... und N weitere (am Dock andocken)" - Overlay ragt nicht mehr ueber den Screenrand.
- Diagnose-Logging (Heartbeat + J-Keydown) aus 289 entfernt.

## Neu in custom-build-290: Jobboard-J-Toggle Fix

- ROOT CAUSE (Diagnose 289): Input.GetKeyDown ist nur einen Frame true; der 0,2s-Debounce vor dem Check hat die meisten Keydowns verschluckt.
- FIX: Toggle-Check laeuft jetzt jeden Frame; Throttle nur noch fuer den 30s-Heartbeat.
- Text-Refresh 1s unveraendert.

## Neu in custom-build-289: Jobboard-Diagnose (J-Toggle reagierte nicht)

- DIAGNOSE: Log-Heartbeat alle 30s bestaetigt, dass CheckToggle() im Spiel-Loop laeuft; jedes J-Keydown wird geloggt (auch ohne Verbindung).
- Jobboard-Feature selbst identisch zu 288.

## Neu in custom-build-288: Boardcomputer-Jobboard (J-Toggle)

- FEATURE: J-Toggle zeigt Screen-Space-Overlay mit den verfuegbaren Jobs des AKTUELLEN Sektors - kein Andocken mehr noetig.
- Anzeige pro Job: Name (QuestInstance.displayName), Credits (JobExtensions.Credits()), Ziel-Bay (JobExtensions.DropOffBay()).
- Datenquelle: ProceduralJobGenerator.GetAvailableJobs() - dieselben Jobs, die JobBoardSync (287) zwischen allen Spielern synchronisiert.
- Hintergrund-Panel + TMP-Klon-Text (gleiche Font-Technik wie DockingBayHUD), Refresh 1s, [J] schliesst.

## Neu in custom-build-287: JobBoardSync Fix — gleiche Auftraege pro Sektor

- BUG: eingehende Job-Syncs wurden beim Empfaenger still verworfen, wenn QuestTracker im Sync-Moment noch nicht ready war (Race nach Sektorwechsel). Jeder Spieler sah sein eigenes, lokal generiertes Board -> Auftraege unterschiedlich zwischen Spielern im gleichen Sektor.
- FIX: HandleIncoming puffert den Sync-Blob bei !QuestTracker.ready (pendingRestoreSector/pendingRestoreJobs) und Client.FixedUpdate() holt ihn ueber JobBoardSync.TryApplyPending() nach, sobald QuestTracker.ready true ist.
- Kein anderes Feature betroffen (identisch zu 286 ausser JobBoardSync-Pfad).

## Neu in custom-build-285: Coordinated Client+Server Deploy (containerType Sync)

- Server leitet containerType jetzt korrekt weiter (Messages.cs, MessageHandler.cs, DedicatedServer.cs).
- PlayerState.TrailerModel wird beim Join-Sync mitgesendet.
- Client: createPlayer NullRef Fix (build-284) bleibt enthalten.
- Backward-Compat: aeltere Clients ohne containerType String crashen nicht.

## Neu in custom-build-284: createPlayer NullRef Fix (Sector Transitions)

- Guard fuer myPlayer/myRigid waehrend Sector-Transitions hinzugefuegt.
- Verhindert NullRef wenn Spieler waehrend Sektorenwechsels ein neues Object erstellt.

## Neu in custom-build-283: Multi-Trailer Protocol (v1 Array-Format)

- trailerMovementUpdate unterstuetzt jetzt v1 Array-Format fuer mehrere Anhaenger pro Spieler.
- Sender erkennt alle CargoContainers im 50m Radius (nicht nur den naechsten) und sendet Array.
- Receiver verwaltet Dictionary<trackingId, GameObject> pro Spieler — spawnt/destroynt Anhaenger dynamisch.
- Backward-compat: v0 Format (1 Trailer) bleibt als Legacy-Fallback erhalten.
- Version-Byte (px < 0 + sentinel) distinguishes v0 vs v1 Format.

## Neu in custom-build-281: Trailer Timing-Race Fix — containerType im Movement-Packet

- containerType wird jetzt direkt im trailerMovementUpdate-Packet mitgesendet (statt als separates reliable Update).
- Behebt den Timing-Race, bei dem die erste Bewegungsmeldung ankam BEVOR der Container-Typ uebertragen wurde.
- Empfaenger liest containerType direkt aus der Bewegungsmeldung und nutzt ihn fuer den Spawn.
- Separiertes updateTrailerModel-Senden wird nicht mehr benoetigt (Backup-Handler bleibt erhalten).

## Neu in custom-build-280: Truck-Spawn-Fix fuer spaet dazukommende Spieler

- Behebt: Andere Spieler sehen nur den Anhanger (~1 Min), dann fliegt der LKW von der Seite rein.
- Ursache: playerConnected schickt keine Position, RemoveFromSector spawnte den Truck bei (0,0,0).
- Fix: Truck wird deferred gespawnt bei der ersten movementUpdate mit gueltiger Position (hard-snap).
- RemoveFromSector spawnt nicht mehr wenn truckTrans.Pos == zero (skip auf movementUpdate).
- truckTargetPos/Rot wird jetzt korrekt vom gespawnten Player zurueckkopiert.

## Neu in custom-build-278: Departure Board — Server-Sync + TMP-Rendering Fix

- Server leitet jetzt destinationGateId in Movement-Updates weiter (war vorher gestrippt).
- Alle Clients sehen jetzt die richtigen Spielernamen auf dem Departure Board (nicht mehr nur FREE).
- Client clientJoin liest und speichert destinationGateId für spät hinzukommende Spieler.
- TMP-Rendering: Font/Material wird immer vom Source kopiert (nicht nur bei null) — behebt IL2CPP-Klon-Bug.
- TMP-Textfarbe explizit Weiss (0.8 Alpha) statt dunkler Default-Inheritance.
- CanvasRenderer.SetAlpha(0.5f) entfernt — machte Text fast unsichtbar auf schwarzem Hintergrund.
- Server-Container-Docker-Image mit neuem Code neugebaut und deployed.

## Neu in custom-build-216: WarpGateBillboard — 3D-Anzeigetafel an WarpGates

- WarpGateHUD (Screen-Space Overlay) komplett ersetzt durch WarpGateBillboard (3D World-Space).
- 500m vor jedem WarpGate erscheint eine grosse Anzeigetafel (15m x 25m, dunkelblau).
- Zeigt ALLE Spieler die das Gate als Ziel haben: "POS 1. Name --- 342m", sortiert nach Distanz.
- "FREE" in Gross + Gruen wenn niemand das Gate als Ziel markiert hat.
- Detection: Spieler innerhalb 1500m + Velocity zeigt zum Gate (Dot-Product > 0.3).
- Billboard rotiert automatisch zur Kamera (BillboardBehavior MonoBehaviour).
- destinationGateId wird pro Movement-Update synchronisiert (backward-compatible, appended).
## Neu in custom-build-216: DockingBay Name Format

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
