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
        setPlayerName
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
        public GameObject NameLabel;
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
