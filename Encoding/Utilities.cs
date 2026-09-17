using System.Collections.Generic;
using UnityEngine;

namespace StarTruckMP.Utilities
{
    public enum messageType
    {
        clientJoin,
        clientDisconnect,
        movementUpdate,
        chatMessage,
        updateSector,
        updateLivery,
        playerConnected,
        trailerMovementUpdate,
        setPlayerName,
        updateTrailerModel,
        setPlayerSteamId,
        requestLinkStatus,
        linkStatus,
        clientVersion,
        // Build-362: Zahl 14 reserviert, alter Blob-Sync-Pfad (FlatSharp Message 14) stillgelegt —
        // ersetzt durch den server-authoritativen Pool (jobBoardUpload 19). Bewusst NUR umbenannt,
        // damit der Compiler jede noch-aktive Call-Site findet (Muster wie bei 18).
        jobBoardSync_deactivated_DO_NOT_USE = 14,
        cargoSync,
        setDestinationGate,
        multiTrailerMovementUpdate,
        // Build-333 (PLAN B Same-Seed lite): Kennungs-Broadcast (questId|displayName Strings).
        // build-341 hat dort jobBoardIdents; ab 342 bleibt die Zahl 18 reserviert (alter Pfad
        // deaktiviert), NEU ab 342 (Server-authoritative Job-Sync):
        jobBoardIdents_deactivated_DO_NOT_USE = 18,
        jobBoardUpload,          // 19: Client -> Server: vollstaendige lokale Job-Liste (chunked)
        jobBoardDownload,        // 20: Server -> Client: Gesamtpool des Sektors (chunked)
        jobTaken,                // 21: AcceptJob-Event (questId) — Client->Server + Broadcast
        pioneerFlag = 22         // 22: Server -> Client: (ulong steamId, bool pioneer) — Pioneer-Badge (custom-build-348)
    }

    public struct playerInfo
    {
        public GameObject Player;
        public GameObject Truck;
        public GameObject Trailer;
        public movementTrans playerTrans;
        public movementTrans truckTrans;
        public movementTrans trailerTrans;
        public bool trailerHitched;
        public string Name;
        public string sector;
        public bool seated;
        public string livery;
        public string trailerModel;
        public Dictionary<long, GameObject> Trailers;
        public GameObject NameLabel;
        public Vector3 trailerSmoothVel;
        public Vector3 trailerTargetPos;
        public Vector3 trailerTargetRot;
        public Dictionary<long, movementTrans> trailerExtraTargets;
        public Vector3 truckTargetPos;
        public Vector3 truckTargetRot;
        public float spawnTime;
        public bool isColliding;
        public bool collisionReady;
        public string destinationGateId;
        // Build 320: Sustained-drift tracking — wie lange der Renderfehler schon > SustainedDriftError liegt.
        public float driftTimer;
        // Trailer-Sync-Fix: getrennter Drift-Timer für den Legacy-Trailer (darf den
        // Truck-Timer NICHT teilen — sonst löst ein Truck-Snap einen Trailer-Snap aus
        // und umgekehrt).
        public float trailerDriftTimer;
        // Pro Extra-Trailer (trackingId) eigener Drift-Timer + SmoothDamp-Velocity.
        public Dictionary<long, float> trailerExtraDriftTimers;
        public Dictionary<long, Vector3> trailerExtraSmoothVels;
        // Zeitstempel (Time.realtimeSinceStartup) des letzten empfangenen Trailer-Targets,
        // für die Velocity-Extrapolation zwischen den 100ms-Updates.
        public float lastTrailerTargetTime;
        // custom-build-348: Pioneer-Badge — SteamID (aus setPlayerSteamId zugeordnet) und
        // Pioneer-Flag (aus pioneerFlag-Nachricht). Nur Namenslabel-Badge, kein Chat/Map.
        public ulong steamId;
        public bool pioneer;
    }

    public struct movementTrans
    {
        public Vector3 Pos;
        public Vector3 Rot;
        public Vector3 Vel;
        public Vector3 AngVel;
        public bool isHonking;
    }

    internal class Utilities
    {
    }
}
