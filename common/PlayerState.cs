using System;

namespace StarTruckMP.Common;

public class PlayerState
{
    public ushort Id { get; set; }
    public string Name { get; set; } = "Unknown";
    public string Sector { get; set; } = "none";
    public bool Seated { get; set; }
    public bool InTruck { get; set; } = true;
    public Vector3f TruckPosition { get; set; }
    public Vector3f TruckRotation { get; set; }
    public Vector3f TruckVelocity { get; set; }
    public Vector3f TruckAngularVelocity { get; set; }
    public Vector3f PlayerPosition { get; set; }
    public Vector3f PlayerRotation { get; set; }
    public Vector3f PlayerVelocity { get; set; }
    public Vector3f PlayerAngularVelocity { get; set; }
    public bool TrailerHitched { get; set; }
    public Vector3f TrailerPosition { get; set; }
    public Vector3f TrailerRotation { get; set; }
    public string Livery { get; set; } = "";
    public string TrailerModel { get; set; } = "";

    // custom-build-367 (Bug A): letzter bekannter MULTI-Trailerzustand, damit der
    // Join-Catchup (OnClientConnected) dem Neu-Joiner ALLE Trailer mit ECHTEM
    // Containertyp schicken kann — vorher wurde nur ein Legacy-Einzeltrailer ohne
    // containerType gesendet (model=''), woraufhin der Client einen Fallback-Mesh
    // (bzw. Placeholder) baute, bis die erste MULTI-Bewegung eintraf.
    public long[] MultiTrailerIds { get; set; }
    public string[] MultiTrailerTypes { get; set; }
    public float[][] MultiTrailerPositions { get; set; }
    public ulong SteamId { get; set; }
    public DateTime LastUpdate { get; set; } = DateTime.UtcNow;
    public string DestinationGateId { get; set; } = "";
    // custom-build-348: Pioneer-Badge (vom Server via pioneerFlag-Nachricht verteilt)
    public bool Pioneer { get; set; }
}
