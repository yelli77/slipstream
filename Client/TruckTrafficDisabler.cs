using System;
using System.Collections.Generic;
using UnityEngine;

namespace StarTruckMP.StarTruckClient
{
    /// <summary>
    /// While connected to a multiplayer server, prevents ambient AI *trucks*
    /// (TrafficType.Truck) from spawning/roaming and despawns any that are already
    /// active. Small ambient vehicles (DroneForklift/DroneMining/DroneMaintenance)
    /// are intentionally left untouched.
    /// Reverts to normal (lets the game re-spawn trucks) as soon as the client
    /// disconnects, via Cleanup().
    /// </summary>
    public static class TruckTrafficDisabler
    {
        private static float lastCheck = 0f;
        private static readonly float CheckInterval = 3f; // seconds between sweeps

        /// <summary>
        /// Called every frame from the Harmony Update patch. Throttled internally.
        /// </summary>
        public static void UpdatePositions()
        {
            try
            {
                if (StarTruckClient.client == null || !StarTruckClient.client.IsConnected) return;

                float now = Time.realtimeSinceStartup;
                if (now - lastCheck < CheckInterval) return;
                lastCheck = now;

                var tm = TrafficManager.Instance;
                if (tm == null) return;

                var traffic = tm.Traffic;
                if (traffic == null) return;

                int trafficCount = traffic.Count;
                for (int i = 0; i < trafficCount; i++)
                {
                    var data = traffic[i];
                    if (data == null) continue;
                    if (data.TrafficType != TrafficType.Truck) continue;

                    // Stop the game from spawning any more trucks.
                    try { data.m_targetVehicleCount = 0; } catch { }

                    // Despawn any trucks that are already active.
                    var active = data.ActiveVehicles;
                    if (active == null) continue;

                    var toDespawn = new List<AIVehicleBase>();
                    int activeCount = active.Count;
                    for (int j = 0; j < activeCount; j++)
                    {
                        var vi = active[j];
                        if (vi == null) continue;
                        var vehicle = vi.vehicleInst;
                        if (vehicle != null) toDespawn.Add(vehicle);
                    }

                    for (int k = 0; k < toDespawn.Count; k++)
                    {
                        try { TrafficManager.DespawnVehicle(toDespawn[k]); }
                        catch (Exception ex) { StarTruckMP.Log.LogWarning($"TruckTrafficDisabler: DespawnVehicle failed: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"TruckTrafficDisabler.UpdatePositions error: {ex}");
            }
        }

        /// <summary>
        /// Called on disconnect: lets the game recompute the normal truck target count
        /// again (offline/singleplayer traffic is unaffected by this feature).
        /// </summary>
        public static void Cleanup()
        {
            try
            {
                lastCheck = 0f;

                var tm = TrafficManager.Instance;
                if (tm == null) return;

                var traffic = tm.Traffic;
                if (traffic == null) return;

                int trafficCount = traffic.Count;
                for (int i = 0; i < trafficCount; i++)
                {
                    var data = traffic[i];
                    if (data == null) continue;
                    if (data.TrafficType != TrafficType.Truck) continue;
                    try { data.RefreshTargetVehicleCount(); } catch { }
                }
            }
            catch (Exception ex)
            {
                StarTruckMP.Log.LogWarning($"TruckTrafficDisabler.Cleanup error: {ex}");
            }
        }
    }
}
