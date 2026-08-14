using System;
using StarTruckMP.Dedicated;

namespace StarTruckMP.Dedicated;

public static class Program
{
    public static void Main(string[] args)
    {
        int port = GetEnvInt("SERVER_PORT", 7777);
        int maxClients = GetEnvInt("MAX_CLIENTS", 8);
        int minClientBuild = GetEnvInt("MIN_CLIENT_BUILD", 151);
        string name = Environment.GetEnvironmentVariable("SERVER_NAME") ?? "StarTruckMP Server";
        Console.WriteLine($"[StarTruckMP Dedicated Server]");
        Console.WriteLine($"  Name:            {name}");
        Console.WriteLine($"  Port:            {port}/UDP");
        Console.WriteLine($"  Max Clients:     {maxClients}");
        Console.WriteLine($"  Min Client Build:{minClientBuild}");
        Console.WriteLine();
        var server = new DedicatedServer(port, maxClients, name, minClientBuild);
        server.Run();
    }
    private static int GetEnvInt(string key, int fallback)
    {
        var val = Environment.GetEnvironmentVariable(key);
        return int.TryParse(val, out int r) ? r : fallback;
    }
}
