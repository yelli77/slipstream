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
        jobBoardSync,
        cargoSync,
        setDestinationGate,
        multiTrailerMovementUpdate
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
