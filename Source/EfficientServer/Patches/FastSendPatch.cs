using System;
using HarmonyLib;
using UnityEngine;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Bang-for-buck #1. ConnectionManager.SendPackage linear-scans the entire
    /// Clients list filtering by entityId to find one recipient; SendToPlayers calls
    /// it once per tracked player and updatePlayerList calls SendToPlayers ~7x per
    /// entity per tick, so replication fan-out is O(entities x players x clients).
    /// This prefix short-circuits ONLY the pure single-target case (send to exactly
    /// one attached entity's client, no other filter mode) via the existing O(1)
    /// entityId map, then reuses the game's own per-client enqueue. Provably
    /// equivalent: entityId is unique per client, so vanilla also enqueues to exactly
    /// one client, giving the identical send-queue refcount trace. That trace matters:
    /// vanilla SendPackage does RegisterSendQueue (+1) BEFORE the filter loop, then an
    /// UNCONDITIONAL SendQueueHandled (-1) after it (AddToSendQueue takes the queue's
    /// own +1, released by the connection writer thread). Skipping the closing
    /// Handled leaves one reference stuck above zero forever, so FreePackage never
    /// fires and each short-circuited package is lost from the pool. The prefix below
    /// replays all three steps exactly. Every other filter mode falls through to
    /// vanilla. Server-internal, no wire change; code -> EAC-off.
    /// </summary>
    [HarmonyPatch(typeof(ConnectionManager), "SendPackage", new[]
    {
        typeof(NetPackage), typeof(bool), typeof(int), typeof(int), typeof(int),
        typeof(Nullable<Vector3>), typeof(int), typeof(bool),
    })]
    public static class FastSendPatch
    {
        static bool Prefix(ConnectionManager __instance, NetPackage _package,
            bool _onlyClientsAttachedToAnEntity, int _attachedToEntityId,
            int _allButAttachedToEntityId, int _entitiesInRangeOfEntity,
            Nullable<Vector3> _entitiesInRangeOfWorldPos, int _range,
            bool _onlyClientsNotAttachedToAnEntity)
        {
            NetworkConfig cfg = ModApi.Config != null ? ModApi.Config.Network : null;
            if (!ModApi.ShouldRun() || cfg == null || !cfg.FastSingleTargetSend) return true;
            if (_package == null) return true;
            // Pure single-target only: one attached entity, no other filter mode.
            if (_attachedToEntityId < 0 || _allButAttachedToEntityId >= 0
                || _entitiesInRangeOfEntity >= 0 || _onlyClientsAttachedToAnEntity
                || _onlyClientsNotAttachedToAnEntity || _entitiesInRangeOfWorldPos.HasValue)
                return true;

            ClientInfoCollection clients = __instance.Clients;
            if (clients == null) return true;
            ClientInfo client = clients.ForEntityId(_attachedToEntityId);
            if (client != null && client.loginDone && client.bAttachedToEntity)
            {
                // Vanilla's exact refcount trace for one matched client:
                // register (+1, holds the package across the send decision),
                // enqueue (AddToSendQueue takes the queue's own +1), then drop
                // our hold (-1). Omitting the final Handled would strand the
                // count above zero and leak every sent package out of the
                // game's package pool.
                _package.RegisterSendQueue();
                client.SendPackage(_package);
                _package.SendQueueHandled();
            }
            // else: no logged-in attached client for this id -> nothing sent (== vanilla)
            return false; // skip the O(clients) scan
        }
    }
}
