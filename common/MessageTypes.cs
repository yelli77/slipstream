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
    LinkStatus = 12
}
