using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Riptide;
using StarTruckMP.Common;

namespace StarTruckMP.Dedicated;

public class DedicatedServer
{
    private readonly Riptide.Server _server;
    private readonly Dictionary<ushort, PlayerState> _players = new();
    private readonly int _maxClients;
    private readonly string _serverName;
    private readonly int _minClientBuild;
    private readonly MessageHandler _handler;
    private DateTime _startTime;

    // Versionscheck: jeder frisch verbundene Client hat ein paar Sekunden Zeit, seine
    // ClientVersion-Nachricht zu schicken. Wer das nicht tut (z.B. eine alte Mod-Version, die
    // dieses Feature noch gar nicht kennt) wird beim Ablauf des Zeitfensters gekickt - genau wie
    // ein Client, der sich meldet, aber eine zu alte Build-Nummer hat.
    private readonly Dictionary<ushort, DateTime> _pendingVersionCheck = new();
    private readonly HashSet<ushort> _versionVerified = new();
    private static readonly TimeSpan VersionCheckTimeout = TimeSpan.FromSeconds(6);

    public DedicatedServer(int port, int maxClients, string serverName, int minClientBuild)
    {
        _maxClients = maxClients;
        _serverName = serverName;
        _minClientBuild = minClientBuild;
        _handler = new MessageHandler(_players, minClientBuild, OnClientVersionVerified, OnClientVersionRejected);
        _server = new Riptide.Server();
        _server.ClientConnected += OnClientConnected;
        _server.ClientDisconnected += OnClientDisconnected;
        _server.MessageReceived += OnMessageReceived;
        _server.Start((ushort)port, (ushort)maxClients);
    }

    private void OnClientVersionVerified(ushort clientId)
    {
        _pendingVersionCheck.Remove(clientId);
        _versionVerified.Add(clientId);
    }

    private void OnClientVersionRejected(ushort clientId, int clientBuild)
    {
        _pendingVersionCheck.Remove(clientId);
        string reason = $"Deine Slipstream-Version ist veraltet (Build {clientBuild}). Bitte aktualisiere auf mindestens Build {_minClientBuild}.";
        Log($"Rejecting client {clientId}: build {clientBuild} < required {_minClientBuild}");
        var msg = Message.Create();
        msg.AddString(reason);
        _server.DisconnectClient(clientId, msg);
    }

    private void CheckVersionTimeouts()
    {
        if (_pendingVersionCheck.Count == 0) return;
        var now = DateTime.UtcNow;
        List<ushort> expired = null;
        foreach (var kv in _pendingVersionCheck)
        {
            if (now - kv.Value > VersionCheckTimeout)
            {
                (expired ??= new List<ushort>()).Add(kv.Key);
            }
        }
        if (expired == null) return;
        foreach (var id in expired)
        {
            _pendingVersionCheck.Remove(id);
            Log($"Rejecting client {id}: no version reported within {VersionCheckTimeout.TotalSeconds:F0}s (outdated client?)");
            string reason = $"Deine Slipstream-Version ist zu alt und wird nicht mehr unterstuetzt. Bitte aktualisiere Slipstream.";
            var msg = Message.Create();
            msg.AddString(reason);
            _server.DisconnectClient(id, msg);
        }
    }

    public void Run()
    {
        _startTime = DateTime.UtcNow;
        Log("Server started. Listening for connections...");
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double acc = 0;
        var lastStatus = DateTime.UtcNow;
        while (!cts.Token.IsCancellationRequested)
        {
            double elapsed = sw.Elapsed.TotalSeconds;
            sw.Restart();
            acc += elapsed;
            while (acc >= 1.0/60.0) { _server.Update(); acc -= 1.0/60.0; }
            CheckVersionTimeouts();
            if ((DateTime.UtcNow - lastStatus).TotalSeconds >= 60)
            {
                lastStatus = DateTime.UtcNow;
                var up = DateTime.UtcNow - _startTime;
                Log($"Status: {_players.Count}/{_maxClients} clients, uptime {up.TotalHours:F0}h{up.Minutes:D2}m{up.Seconds:D2}s");
            }
            Thread.Sleep(1);
        }
        _server.Stop();
        Log("Server stopped.");
    }

    private void OnClientConnected(object sender, ServerConnectedEventArgs e)
    {
        Log($"Client connected: {e.Client.Id}");
        _pendingVersionCheck[e.Client.Id] = DateTime.UtcNow;
        var p = new PlayerState { Id = e.Client.Id, Sector = "none" };
        var joinMsg = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.ClientJoin);
        joinMsg.AddUShorts(_players.Keys.ToArray());
        foreach (var pl in _players.Values)
        {
            joinMsg.AddFloat(pl.TruckPosition.X); joinMsg.AddFloat(pl.TruckPosition.Y); joinMsg.AddFloat(pl.TruckPosition.Z);
            joinMsg.AddFloat(pl.TruckRotation.X); joinMsg.AddFloat(pl.TruckRotation.Y); joinMsg.AddFloat(pl.TruckRotation.Z);
            joinMsg.AddString(pl.Sector);
            joinMsg.AddString(pl.Name ?? "");
        }
        _server.Send(joinMsg, e.Client);
        foreach (var kv in _players)
        {
            if (kv.Value.TrailerHitched)
            {
                _server.Send(ServerMessages.CreateTrailerMovement(kv.Key, true, kv.Value.TrailerPosition, kv.Value.TrailerRotation), e.Client);
                if (!string.IsNullOrEmpty(kv.Value.TrailerModel))
                {
                    var tmMsg = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.UpdateTrailerModel);
                    tmMsg.AddUShort(kv.Key); tmMsg.AddString(kv.Value.TrailerModel);
                    _server.Send(tmMsg, e.Client);
                }
            }
        }
        _players.Add(e.Client.Id, p);
        var bc = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.PlayerConnected);
        bc.AddUShort(e.Client.Id);
        bc.AddString(p.Name ?? "");
        _server.SendToAll(bc, e.Client.Id);
    }

    private void OnClientDisconnected(object sender, ServerDisconnectedEventArgs e)
    {
        Log($"Client disconnected: {e.Client.Id} ({e.Reason})");
        _pendingVersionCheck.Remove(e.Client.Id);
        _versionVerified.Remove(e.Client.Id);
        if (_players.TryGetValue(e.Client.Id, out var disconnectedPlayer))
        {
            _handler.NotifyPlayerDisconnected(disconnectedPlayer.SteamId);
        }
        _players.Remove(e.Client.Id);
        var msg = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.ClientDisconnect);
        msg.AddUShort(e.Client.Id);
        _server.SendToAll(msg);
    }

    private void OnMessageReceived(object sender, MessageReceivedEventArgs e)
    {
        _handler.Handle(e, _server);
    }

    private static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
}
