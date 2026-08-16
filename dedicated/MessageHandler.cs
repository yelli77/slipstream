using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Riptide;
using StarTruckMP.Common;
using System.Text.Json;

namespace StarTruckMP.Dedicated;

public class MessageHandler
{
    private static readonly string BridgeBaseUrl = Environment.GetEnvironmentVariable("STARTTRUCKMP_BRIDGE_URL") ?? "http://localhost:4500";
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    private static float _lastBridgeWarnTime = 0f;
    private static float _lastLinkWarnTime = 0f;
    private readonly Dictionary<ushort, PlayerState> _players;
    private readonly int _minClientBuild;
    private readonly Action<ushort> _onVersionVerified;
    private readonly Action<ushort, int> _onVersionRejected;

    public MessageHandler(Dictionary<ushort, PlayerState> players, int minClientBuild, Action<ushort> onVersionVerified, Action<ushort, int> onVersionRejected)
    {
        _players = players;
        _minClientBuild = minClientBuild;
        _onVersionVerified = onVersionVerified;
        _onVersionRejected = onVersionRejected;
    }

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
            case MessageType.SetPlayerName: HandleSetName(e, server); break;
            case MessageType.UpdateTrailerModel: HandleTrailerModel(e, server); break;
            case MessageType.SetPlayerSteamId: HandleSteamId(e, server); break;
            case MessageType.ChatMessage: HandleChatMessage(e, server); break;
            case MessageType.RequestLinkStatus: HandleRequestLinkStatus(e, server); break;
            case MessageType.ClientVersion: HandleClientVersion(e, server); break;
            case MessageType.JobBoardSync: HandleJobBoardSync(e, server); break;
            case MessageType.CargoSync: HandleCargoSync(e, server); break;
        }
        }
        catch (System.Exception ex)
        {
            Console.WriteLine($"[WARN] MessageHandler error: {ex.Message}");
        }
    }

    private void HandleClientVersion(MessageReceivedEventArgs e, Riptide.Server server)
    {
        e.Message.GetUShort();
        int clientBuild = e.Message.GetInt();
        Console.WriteLine($"[INFO] Client {e.FromConnection.Id} reports build {clientBuild}");
        if (clientBuild < _minClientBuild)
        {
            _onVersionRejected(e.FromConnection.Id, clientBuild);
        }
        else
        {
            _onVersionVerified(e.FromConnection.Id);
        }
    }

    // Reiner Relay: der dedizierte Server fuehrt keine Unity/Job-Logik aus und kann das
    // Jobboard-Payload inhaltlich nicht validieren/verstehen. Er liest nur den Sektornamen
    // (fuers Server-Log) und reicht den Job-Blob unveraendert als Bytes weiter (Sender
    // ausgeschlossen, der hat seinen eigenen Stand schon lokal). Reliable, weil ein verlorenes
    // Jobboard-Update zu dauerhaft widerspruechlichen Missionslisten fuehren wuerde.
    private void HandleJobBoardSync(MessageReceivedEventArgs e, Riptide.Server server)
    {
        string sector = e.Message.GetString();
        byte[] jobBlob = e.Message.GetBytes();
        var msg = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.JobBoardSync);
        msg.AddString(sector);
        msg.AddBytes(jobBlob);
        server.SendToAll(msg, e.FromConnection.Id);
    }

    // Gleiches Prinzip wie HandleJobBoardSync: reiner Relay, Inhalt ist reine Client-Sache.
    // Muss VOR den zugehoerigen JobBoardSync-Nachrichten beim Empfaenger ankommen, damit die
    // dort referenzierten Cargo-TrackingIds beim Anwenden des Jobboards bereits existieren -
    // das ist ueber die Sendereihenfolge auf Client-Seite sichergestellt (Cargo wird vor den
    // Jobs generiert), Riptide liefert Reliable-Nachrichten in Sendereihenfolge aus.
    private void HandleCargoSync(MessageReceivedEventArgs e, Riptide.Server server)
    {
        string sector = e.Message.GetString();
        byte[] cargoBlob = e.Message.GetBytes();
        var msg = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.CargoSync);
        msg.AddString(sector);
        msg.AddBytes(cargoBlob);
        server.SendToAll(msg, e.FromConnection.Id);
    }

    private void HandleMovement(MessageReceivedEventArgs e, Riptide.Server server)
    {
        if (!_players.TryGetValue(e.FromConnection.Id, out var p)) return;
        e.Message.GetUShort();
        float[] t = e.Message.GetFloats();
        var pos=new Vector3f(t[0],t[1],t[2]); var rot=new Vector3f(t[3],t[4],t[5]);
        var vel=new Vector3f(t[6],t[7],t[8]); var ang=new Vector3f(t[9],t[10],t[11]);
        bool isTruck=e.Message.GetBool(); bool inSeat=e.Message.GetBool(); bool isHonking=e.Message.GetBool();
        if(isTruck){p.TruckPosition=pos;p.TruckRotation=rot;p.TruckVelocity=vel;p.TruckAngularVelocity=ang;
            if(inSeat){p.PlayerPosition=pos;p.PlayerRotation=rot;p.PlayerVelocity=vel;p.PlayerAngularVelocity=ang;}}
        else{p.PlayerPosition=pos;p.PlayerRotation=rot;p.PlayerVelocity=vel;p.PlayerAngularVelocity=ang;}
        p.InTruck=isTruck;p.Seated=inSeat;p.LastUpdate=DateTime.UtcNow;
        _players[e.FromConnection.Id]=p;
        server.SendToAll(ServerMessages.CreateMovement(e.FromConnection.Id,pos,rot,vel,ang,isTruck,inSeat,isHonking));
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

        // Notify Discord bridge about sector change (fire-and-forget)
        string sectorJson = JsonSerializer.Serialize(new { steamId = p.SteamId.ToString(), sector = sector, name = p.Name });
        _ = PostBridge($"{BridgeBaseUrl}/move", sectorJson);
    }

    private void HandleLivery(MessageReceivedEventArgs e, Riptide.Server server)
    {
        if (!_players.TryGetValue(e.FromConnection.Id, out var p)) return;
        e.Message.GetUShort();
        string item=e.Message.GetString();
        p.Livery=item;p.LastUpdate=DateTime.UtcNow;
        _players[e.FromConnection.Id]=p;
        var msg = Message.Create(MessageSendMode.Unreliable,(ushort)MessageType.UpdateLivery);
        msg.AddUShort(e.FromConnection.Id); msg.AddString(item);
        server.SendToAll(msg);
    }

    private void HandleSetName(MessageReceivedEventArgs e, Riptide.Server server)
    {
        if (!_players.TryGetValue(e.FromConnection.Id, out var p)) return;
        e.Message.GetUShort();
        string name = e.Message.GetString();
        p.Name = name;
        _players[e.FromConnection.Id] = p;
        Console.WriteLine($"[INFO] Player {e.FromConnection.Id} name set to {name}");
        var msg = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.SetPlayerName);
        msg.AddUShort(e.FromConnection.Id);
        msg.AddString(name);
        server.SendToAll(msg);
    }

    private void HandleTrailerModel(MessageReceivedEventArgs e, Riptide.Server server)
    {
        if (!_players.TryGetValue(e.FromConnection.Id, out var p)) return;
        e.Message.GetUShort();
        string model = e.Message.GetString();
        p.TrailerModel = model; p.LastUpdate = DateTime.UtcNow;
        _players[e.FromConnection.Id] = p;
        var msg = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.UpdateTrailerModel);
        msg.AddUShort(e.FromConnection.Id); msg.AddString(model);
        server.SendToAll(msg);
    }

    private void HandleSteamId(MessageReceivedEventArgs e, Riptide.Server server)
    {
        if (!_players.TryGetValue(e.FromConnection.Id, out var p)) return;
        e.Message.GetUShort();
        ulong steamId = e.Message.GetULong();
        p.SteamId = steamId;
        _players[e.FromConnection.Id] = p;
        Console.WriteLine($"[INFO] Player {e.FromConnection.Id} SteamID set to {steamId}");

        // Register presence with the Discord bridge immediately on connect —
        // do not wait for a sector change, some players may sit in one sector
        // for a long time and would otherwise never become linkable.
        string seenJson = JsonSerializer.Serialize(new { steamId = p.SteamId.ToString(), name = p.Name });
        _ = PostBridge($"{BridgeBaseUrl}/player-seen", seenJson);
    }

    private void HandleChatMessage(MessageReceivedEventArgs e, Riptide.Server server)
    {
        if (!_players.TryGetValue(e.FromConnection.Id, out var p)) return;
        string chatMsg = e.Message.GetString().Trim();
        Console.WriteLine($"[CHAT] {e.FromConnection.Id} ({p.Name}): {chatMsg}");

        // !link command — send link-confirm to Discord bridge
        if (chatMsg.StartsWith("!link ", StringComparison.OrdinalIgnoreCase))
        {
            string code = chatMsg.Substring(6).Trim();
            if (!string.IsNullOrEmpty(code))
            {
                string linkJson = JsonSerializer.Serialize(new { code = code, steamId = p.SteamId.ToString() });
                _ = PostBridge($"{BridgeBaseUrl}/link-confirm", linkJson);
            }
        }
    }

    private void HandleRequestLinkStatus(MessageReceivedEventArgs e, Riptide.Server server)
    {
        if (!_players.TryGetValue(e.FromConnection.Id, out var p)) return;
        e.Message.GetUShort();
        _ = CheckAndReplyLinkStatus(p.SteamId, e.FromConnection, server);
    }

    private static async Task CheckAndReplyLinkStatus(ulong steamId, Riptide.Connection connection, Riptide.Server server)
    {
        try
        {
            var response = await _http.GetAsync($"{BridgeBaseUrl}/link-status/{steamId}");
            string body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            bool linked = doc.RootElement.TryGetProperty("linked", out var linkedProp) && linkedProp.GetBoolean();
            server.Send(ServerMessages.CreateLinkStatus(linked), connection);
        }
        catch (Exception ex)
        {
            float now = (float)DateTime.UtcNow.TimeOfDay.TotalSeconds;
            if (now - _lastBridgeWarnTime > 30f)
            {
                _lastBridgeWarnTime = now;
                Console.WriteLine($"[WARN] Bridge GET link-status failed: {ex.Message}");
            }
        }
    }

    public void NotifyPlayerDisconnected(ulong steamId)
    {
        if (steamId == 0) return;
        string json = JsonSerializer.Serialize(new { steamId = steamId.ToString() });
        _ = PostBridge($"{BridgeBaseUrl}/player-disconnect", json);
    }

    private static async Task PostBridge(string url, string json)
    {
        try
        {
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            await _http.PostAsync(url, content);
        }
        catch (Exception ex)
        {
            // Rate-limit warning: max once per 30 seconds
            float now = (float)DateTime.UtcNow.TimeOfDay.TotalSeconds;
            if (now - _lastBridgeWarnTime > 30f)
            {
                _lastBridgeWarnTime = now;
                Console.WriteLine($"[WARN] Bridge POST failed ({url}): {ex.Message}");
            }
        }
    }
}
