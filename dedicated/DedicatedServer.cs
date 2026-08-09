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
    private readonly MessageHandler _handler;
    private DateTime _startTime;

    public DedicatedServer(int port, int maxClients, string serverName)
    {
        _maxClients = maxClients;
        _serverName = serverName;
        _handler = new MessageHandler(_players);
        _server = new Riptide.Server();
        _server.ClientConnected += OnClientConnected;
        _server.ClientDisconnected += OnClientDisconnected;
        _server.MessageReceived += OnMessageReceived;
        _server.Start((ushort)port, (ushort)maxClients);
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
        var p = new PlayerState { Id = e.Client.Id, Sector = "none" };
        var joinMsg = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.ClientJoin);
        joinMsg.AddUShorts(_players.Keys.ToArray());
        foreach (var pl in _players.Values)
        {
            joinMsg.AddFloat(pl.TruckPosition.X); joinMsg.AddFloat(pl.TruckPosition.Y); joinMsg.AddFloat(pl.TruckPosition.Z);
            joinMsg.AddFloat(pl.TruckRotation.X); joinMsg.AddFloat(pl.TruckRotation.Y); joinMsg.AddFloat(pl.TruckRotation.Z);
            joinMsg.AddString(pl.Sector);
        }
        _server.Send(joinMsg, e.Client);
        foreach (var kv in _players)
        {
            if (kv.Value.TrailerHitched)
                _server.Send(ServerMessages.CreateTrailerMovement(kv.Key, true, kv.Value.TrailerPosition, kv.Value.TrailerRotation), e.Client);
        }
        _players.Add(e.Client.Id, p);
        var bc = Message.Create(MessageSendMode.Reliable, (ushort)MessageType.PlayerConnected);
        bc.AddUShort(e.Client.Id);
        _server.SendToAll(bc, e.Client.Id);
    }

    private void OnClientDisconnected(object sender, ServerDisconnectedEventArgs e)
    {
        Log($"Client disconnected: {e.Client.Id} ({e.Reason})");
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
