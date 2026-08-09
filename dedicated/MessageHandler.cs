using System;
using System.Collections.Generic;
using Riptide;
using StarTruckMP.Common;

namespace StarTruckMP.Dedicated;

public class MessageHandler
{
    private readonly Dictionary<ushort, PlayerState> _players;
    public MessageHandler(Dictionary<ushort, PlayerState> players) { _players = players; }

    public void Handle(MessageReceivedEventArgs e, Riptide.Server server)
    {
        try
        {
        switch ((MessageType)e.MessageId)
        {
            case MessageType.MovementUpdate: HandleMovement(e, server); break;
            case MessageType.TrailerMovementUpdate: HandleTrailer(e, server); break;
            case MessageType.UpdateSector: HandleSector(e, server); break;
            case MessageType.UpdateLivery: HandleLivery(e, server); break;
            case MessageType.ChatMessage: Console.WriteLine($"[CHAT] {e.FromConnection.Id}: {e.Message.GetString()}"); break;
        }
        }
        catch (System.Exception ex)
        {
            Console.WriteLine($"[WARN] MessageHandler error: {ex.Message}");
        }
    }

    private void HandleMovement(MessageReceivedEventArgs e, Riptide.Server server)
    {
        if (!_players.TryGetValue(e.FromConnection.Id, out var p)) return;
        e.Message.GetUShort();
        float[] t = e.Message.GetFloats();
        var pos=new Vector3f(t[0],t[1],t[2]); var rot=new Vector3f(t[3],t[4],t[5]);
        var vel=new Vector3f(t[6],t[7],t[8]); var ang=new Vector3f(t[9],t[10],t[11]);
        bool isTruck=e.Message.GetBool(); bool inSeat=e.Message.GetBool();
        if(isTruck){p.TruckPosition=pos;p.TruckRotation=rot;p.TruckVelocity=vel;p.TruckAngularVelocity=ang;
            if(inSeat){p.PlayerPosition=pos;p.PlayerRotation=rot;p.PlayerVelocity=vel;p.PlayerAngularVelocity=ang;}}
        else{p.PlayerPosition=pos;p.PlayerRotation=rot;p.PlayerVelocity=vel;p.PlayerAngularVelocity=ang;}
        p.InTruck=isTruck;p.Seated=inSeat;p.LastUpdate=DateTime.UtcNow;
        _players[e.FromConnection.Id]=p;
        server.SendToAll(ServerMessages.CreateMovement(e.FromConnection.Id,pos,rot,vel,ang,isTruck,inSeat));
    }

    private void HandleTrailer(MessageReceivedEventArgs e, Riptide.Server server)
    {
        if (!_players.TryGetValue(e.FromConnection.Id, out var p)) return;
        e.Message.GetUShort();
        bool hitched=e.Message.GetBool(); float[] t=e.Message.GetFloats();
        var pos=new Vector3f(t[0],t[1],t[2]); var rot=new Vector3f(t[3],t[4],t[5]);
        p.TrailerHitched=hitched;p.TrailerPosition=pos;p.TrailerRotation=rot;p.LastUpdate=DateTime.UtcNow;
        _players[e.FromConnection.Id]=p;
        server.SendToAll(ServerMessages.CreateTrailerMovement(e.FromConnection.Id,hitched,pos,rot));
    }

    private void HandleSector(MessageReceivedEventArgs e, Riptide.Server server)
    {
        if (!_players.TryGetValue(e.FromConnection.Id, out var p)) return;
        e.Message.GetUShort(); string sector=e.Message.GetString();
        p.Sector=sector;p.LastUpdate=DateTime.UtcNow;
        _players[e.FromConnection.Id]=p;
        var msg=Message.Create(MessageSendMode.Reliable,(ushort)MessageType.UpdateSector);
        msg.AddUShort(e.FromConnection.Id); msg.AddString(sector);
        server.SendToAll(msg);
    }

    private void HandleLivery(MessageReceivedEventArgs e, Riptide.Server server)
    {
        if (!_players.TryGetValue(e.FromConnection.Id, out var p)) return;
        e.Message.GetUShort();
        string item=e.Message.GetString();
        p.Livery=item;p.LastUpdate=DateTime.UtcNow;
        _players[e.FromConnection.Id]=p;
        var msg=Message.Create(MessageSendMode.Unreliable,(ushort)MessageType.UpdateLivery);
        msg.AddUShort(e.FromConnection.Id); msg.AddString(item);
        server.SendToAll(msg);
    }
}
