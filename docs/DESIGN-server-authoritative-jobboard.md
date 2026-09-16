# StarTruckMP custom-build-342 — Server-authoritative Job-Sync (Design)

Status: implementiert auf `feature/server-authoritative-jobboard-342` (KEIN Release — Release macht architect nach Review).

## Motivation

Alle bisherigen Client-zu-Client-Job-Sync-Ansaetze (325/326/327 Blob, 328-335 native
FlatSharp-Restore, 333-341 Kennungs-Filter) scheiterten strukturell: jeder Client
generiert lokal eigene Jobs, FlatSharp-Union-Serialisierung ist IL2CPP-fehleranfaellig
(`Discriminator = 0`), und Client-Relay + Authority-Convention (niedrigste ID) erzeugen
Split-Brain (stale Sektor-Tags, konkurrierende Sets, match 0/N).

Neuerarbeitung (mit Michael abgestimmt, inkl. Design-Erweiterung): der **Dedicated
Server ist die Autoritaet** fuer Jobboards pro Sektor.

## Architektur

- **JobBoardUpload** (Client → Server): ALLE Clients im Sektor schicken ihre jeweils
  lokal generierte Job-Liste (nicht nur der "erste") — bei Sektor-Betreten und wenn
  lokale Generierung neue Jobs erzeugt. Der Server merged sie in einen
  **Gesamtpool pro Sektor** (Vereinigung, dedupe nach questId).
- **JobBoardDownload** (Server → Client): Server schickt Pool-Updates an alle Clients
  im Sektor. **Voll-Pool** (kein Delta — einfacher, idempotent, selbstheilend;
  ~200 Jobs ≈ 10-15 KB komprimierbar, chunked transfer). Clients rendern den Pool
  statt ihrer lokalen Liste.
- **Fallback**: Solo / bevor ein Pool ankommt bleibt die lokale Generierung unangetastet.
  Gate: alle Sync-Features nur bei `client.IsConnected`.
- **JobTaken**: AcceptJob broadcastet der Server als Event mit questId an alle Clients
  im Sektor; der Job verschwindet aus dem Pool → bei allen aus dem Board.
- **Edge-Cases**:
  - Client kommt spaeter dazu → bekommt den vollen Pool beim Sektor-Betreten.
  - Client verlaesst Sektor → Pool bleibt bestehen (Jobboards sind Weltzustand,
    nicht spielergebunden).
  - Server-Restart → Pool leer; erste Uploads fuellen ihn wieder.
  - Client bleibt allein im Sektor → er ist Autoritaet des Boards (eigene Liste),
    bis ein Pool-Update eintrifft.

## Message-Flow / Reihenfolge

1. Client betritt Sektor (`UpdateSector`) → Server merkt sich Sektor je Player.
2. Client generiert lokal Jobs (Spiel-Pfad unangetastet). Postfix auf
   `ProceduralJobGenerator.GenerateJobsForAllSectors` + Sektor-Betreten triggern
   `JobBoardUpload.SendCurrentBoard()`.
3. Upload: explizite BinaryWriter-Serialisierung **nur der Felder, die das Board
   zum Anzeigen/Accept braucht** (questId, displayName, displayDescription,
   generatedParameters als einfachgetippte name/kind/value-Primitiva — KEINE
   FlatSharp-Union-Objekte, keine QuestInstanceSaveData-Objekte ueber die Leitung).
   Chunked via ChunkedBlobTransfer-artigem eigenen Protokoll (Riptide-Messages,
   reliable, gepaced).
4. Server merged (dedupe questId), speichert In-Memory pro Sektor
   (`JobBoardStore`), broadcastet neuen Pool an alle Clients im Sektor.
5. Client empfaengt Pool → wendet ihn frame-budgetiert an
   (`SetAvailableJobs`/`InjectAvailableJob` ueber mehrere Frames, max N Jobs/Frame,
   Main-Thread wird nicht blockiert — Freeze-Lektion 336/340).
6. AcceptJob (jeder Client lokal): Client sendet `JobTaken(questId)` an Server;
   Server entfernt den Job aus dem Pool + broadcastet `JobTaken` → alle Clients
   entfernen den Job aus dem Board (frame-budgetiert).

## Ersetzte / deaktivierte alte Pfade

- `JobBoardSync` (Blob, FlatSharp v2): **entfernt** aus dem Sende-/Empfangspfad
  (Handler-Hook bleibt als No-Op-Kommentar; MessageType-Zahlen bleiben reserviert,
  damit alte Clients nicht in andere Handler rutschen).
- `JobBoardIdSync` (Kennungs-Filter): **entfernt** — kein Doppel-Broadcast mehr.
- MessageTypes: neue Nummern `jobBoardUpload` / `jobBoardDownload` / `jobTaken`
  (reservierte Luecken im client-seitigen messageType-Enum gewahrt — die client-
  seitigen Zahlen 16/17 sind Legacy und muessen stabil bleiben).

## Serialisierung (Warum keine FlatSharp)

FlatSharp-Union (QuestTaskParameterSaveData) crashte in IL2CPP zuverlaessig
(`Discriminator = 0`, 326/327/335-Logs). Neuer Weg: eigene explizite Binaer-
Serialisierung ueber BinaryWriter/BinaryReader (Vorbild CargoSync in
Client/CargoSync.cs). Nur benoetigte Felder (aus reference/api-dump verifiziert:
QuestInstance.questId/displayName/displayDescription, QuestTaskParameter.name/type
+ Primitiva). Parameter-Objekte werden als (name, kind, primitiveValue)-Tripel
uebertragen; komplexe Kinds (cargoProperties, conversation, quest, inventoryItemTags,
ventureLocation, ventureType, questFlag) werden als stringValue-Repraesentation
bzw. ganz weggelassen — Accept bleibt funktionabel, da die generierten Jobs auf dem
Empfaenger per questId gegen seine eigene lokal generierte Liste gematcht werden
(questId ist sprach- und seed-stabil, Beweis custom-build-339/341).

**Accept-Pfad**: Empfnger hat die QuestInstance-Objekte lokal schon generiert —
der Pool liefert die *Sicht* (welche questIds sind sichtbar/annehmbar). AcceptJob
selbst ruft die SPIELEIGENE AcceptJob-Logik mit dem lokalen QuestInstance-Objekt
(dieselbe questId) — keine Spielobjekte muessen ueber die Leitung.
