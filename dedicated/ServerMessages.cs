using Riptide;
using StarTruckMP.Common;

namespace StarTruckMP.Dedicated;

public static class ServerMessages
{
    public static Message CreateMovement(ushort playerId, Vector3f pos, Vector3f rot, Vector3f vel, Vector3f angVel, bool isTruck, bool inSeat, bool isHonking = false, string destinationGateId = "")
    {
        float[] t = { pos.X,pos.Y,pos.Z, rot.X,rot.Y,rot.Z, vel.X,vel.Y,vel.Z, angVel.X,angVel.Y,angVel.Z };
        var msg = Message.Create(MessageSendMode.Unreliable, (ushort)MessageType.MovementUpdate);
        msg.AddUShort(playerId); msg.AddFloats(t); msg.AddBool(isTruck); msg.AddBool(inSeat); msg.AddBool(isHonking);
        msg.AddString(destinationGateId ?? "");
        return msg;
    }

    // custom-build-356 (FIX D): Server->Clients-Broadcast der SteamID-Zuordnung.
    // Ohne dieses Broadcast bleibt SteamIdByPlayer auf fremden Clients leer.
    public static Message CreatePlayerSteamIdBcast(ushort playerId, ulong steamId)
    {
        var msg = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.SetPlayerSteamId);
        msg.AddUShort(playerId); msg.AddULong(steamId);
        return msg;
    }

    public static Message CreateLinkStatus(bool linked)
    {
        var msg = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.LinkStatus);
        msg.AddBool(linked);
        return msg;
    }

        // custom-build-366 (Bug 1): v1 Multi-Trailer-Format 1:1 re-encodieren. Vorher hat
    // HandleTrailer jede MULTI-Nutzlast als Legacy-Single-Trailer geparst und als
    // 6-Float-Einzeltrailer mit containerType='MULTI' neu gebaut — der Empfaenger
    // erkannte zwar isMultiTrailer, lief aber in eine leere Message (GetLong auf
    // erschöpftem Buffer) -> Exception -> Trailer sprawnete NIE.
    public static Message CreateMultiTrailerMovement(ushort playerId, ushort count,
        long[] trackingIds, string[] containerTypes, float[][] positions)
    {
        var msg = Message.Create(MessageSendMode.Unreliable, (ushort)MessageType.TrailerMovementUpdate);
        msg.AddUShort(playerId);
        msg.AddBool(true);
        msg.AddFloats(new float[] { -1f, (float)count, 0f, 0f, 0f, 0f });
        msg.AddString("MULTI");
        msg.AddUShort(count);
        for (int i = 0; i < count; i++)
        {
            msg.AddLong(trackingIds[i]);
            msg.AddString(containerTypes[i] ?? "");
            msg.AddFloats(positions[i]);
        }
        return msg;
    }

    public static Message CreateTrailerMovement(ushort playerId, bool hitched, Vector3f pos, Vector3f rot, string containerType = "")
    {
        float[] t = { pos.X,pos.Y,pos.Z, rot.X,rot.Y,rot.Z };
        var msg = Message.Create(MessageSendMode.Unreliable, (ushort)MessageType.TrailerMovementUpdate);
        msg.AddUShort(playerId); msg.AddBool(hitched); msg.AddFloats(t); msg.AddString(containerType ?? "");
        return msg;
    }
}
