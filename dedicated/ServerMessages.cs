using Riptide;
using StarTruckMP.Common;

namespace StarTruckMP.Dedicated;

public static class ServerMessages
{
    public static Message CreateMovement(ushort playerId, Vector3f pos, Vector3f rot, Vector3f vel, Vector3f angVel, bool isTruck, bool inSeat, bool isHonking = false)
    {
        float[] t = { pos.X,pos.Y,pos.Z, rot.X,rot.Y,rot.Z, vel.X,vel.Y,vel.Z, angVel.X,angVel.Y,angVel.Z };
        var msg = Message.Create(MessageSendMode.Unreliable, (ushort)MessageType.MovementUpdate);
        msg.AddUShort(playerId); msg.AddFloats(t); msg.AddBool(isTruck); msg.AddBool(inSeat); msg.AddBool(isHonking);
        return msg;
    }

    public static Message CreateLinkStatus(bool linked)
    {
        var msg = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.LinkStatus);
        msg.AddBool(linked);
        return msg;
    }

        public static Message CreateTrailerMovement(ushort playerId, bool hitched, Vector3f pos, Vector3f rot)
    {
        float[] t = { pos.X,pos.Y,pos.Z, rot.X,rot.Y,rot.Z };
        var msg = Message.Create(MessageSendMode.Unreliable, (ushort)MessageType.TrailerMovementUpdate);
        msg.AddUShort(playerId); msg.AddBool(hitched); msg.AddFloats(t);
        return msg;
    }
}
