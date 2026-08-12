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
        updateTrailerModel
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
        public GameObject NameLabel;
        public Vector3 trailerSmoothVel;
        public Vector3 trailerTargetPos;
        public Vector3 trailerTargetRot;
        public Vector3 truckTargetPos;
        public Vector3 truckTargetRot;
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
