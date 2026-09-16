namespace StarTruckMP.Common;

public enum MessageType : ushort
{
    ClientJoin = 0,
    ClientDisconnect = 1,
    MovementUpdate = 2,
    ChatMessage = 3,
    UpdateSector = 4,
    UpdateLivery = 5,
    PlayerConnected = 6,
    TrailerMovementUpdate = 7,
    SetPlayerName = 8,
    UpdateTrailerModel = 9,
    SetPlayerSteamId = 10,
    RequestLinkStatus = 11,
    LinkStatus = 12,
    ClientVersion = 13,
    JobBoardSync = 14,
    CargoSync = 15,
    // Build-333 (PLAN B Same-Seed lite): Kennungs-Broadcast (questId|displayName Strings).
    // ACHTUNG: Client-seitig (Encoding/Utilities.cs messageType) liegen setDestinationGate=16
    // und multiTrailerMovementUpdate=17 dazwischen (Legacy-Eintraege, Server-ohne-Handler) -
    // jobBoardIdents ist dort 18. Server-ID MUSS 18 sein, nicht 16!
    // Build-342: Server-authoritative Job-Sync. Alte Pfade (JobBoardSync-Blob = 14,
    // JobBoardIdents = 18) werden deaktiviert, die NUMMERN bleiben reserviert, damit alte
    // Clients nicht in fremde Handler rutschen. Neue Nummern ab 19:
    jobBoardUpload = 19,     // Client -> Server: volle lokal generierte Job-Liste des Sektors
    jobBoardDownload = 20,   // Server -> Client: vollstaendiger Gesamtpool des Sektors
    jobTaken = 21            // beide Richtungen: AcceptJob-Event (questId)
}
