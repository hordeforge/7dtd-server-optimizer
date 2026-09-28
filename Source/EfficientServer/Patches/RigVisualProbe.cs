using System;
using System.Collections.Generic;
using UnityEngine;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Bench probe #2 (RE sweep 3n): disable/enable unguarded visual MonoBehaviours
    /// on entity rigs, to size their per-frame cost without new assembly
    /// references. Eyelid blink, gaze, feather flutter, held-light raycast, drone
    /// lights. Visual-only per RE; <c>RagdollWhenHit</c> is deliberately excluded
    /// because it touches physics. Driven by `es rigoff` / `es rigon`, and
    /// released by a config reload that takes
    /// <see cref="DiagnosticsConfig.AllowFidelityProbes"/> back.
    ///
    /// GAMEPLAY VISUALS DEGRADE WHILE OFF (server-wide). The arm gate is the
    /// console command's, not this module's: both fidelity probes gate on the same
    /// opt-in and both audit a refusal, so the decision stays where the operator
    /// types it.
    ///
    /// A repeated <c>rigoff</c> converges to the same state as one sweep and a
    /// single <c>rigon</c> undoes all of it: only still-enabled, untracked
    /// components are disabled, so the tracking is cumulative across calls and
    /// cannot drop an earlier batch.
    ///
    /// Internal like the other support modules (<see cref="AnimatorEmergency"/>,
    /// <see cref="AiAlertGate"/>, <see cref="TickClock"/>): runtime state with
    /// in-assembly consumers only (the es console), not a Harmony patch group and
    /// not game-discovered, so it stays off the assembly's public surface.
    /// </summary>
    internal static class RigVisualProbe
    {
        static readonly HashSet<string> RigTypes = new HashSet<string> {
            "EyeLidController", "CharacterGazeController", "FeatherFlutter",
            "LightLODHeld", "DroneRunningLight", "DroneBeamParticle",
        };
        // Components this probe disabled and has not yet restored, across ALL
        // `es rigoff` calls since the last `es rigon`. Cumulative by design: a
        // repeated rigoff must not drop the first batch from tracking, or
        // `es rigon` would restore only the newest sweep and leave every earlier
        // component disabled until restart. Destroyed rigs are pruned at every
        // sweep (PruneDespawnedTracked), so the bound is "live components",
        // not lifetime spawns.
        static readonly List<Behaviour> _disabled = new List<Behaviour>();
        // Membership mirror of _disabled, so the sweep tests "already tracked" in
        // O(1) instead of a linear List.Contains per candidate. The sweep walks
        // every Behaviour on every entity's rig, so the linear form was
        // O(components x already-disabled): a repeated rigoff over a large horde
        // re-tests the whole tracked set for every component it visits. Kept in
        // lockstep by Add/Exit below; PruneDespawnedTracked removes from both.
        // UnityEngine.Object overrides Equals, so the set uses the same
        // destroyed-object semantics as List.Contains.
        static readonly HashSet<Behaviour> _disabledSet = new HashSet<Behaviour>();
        // Type -> is that type one of the rig types above. The sweep tests EVERY
        // Behaviour on EVERY entity's rig, and the test used to be
        // RigTypes.Contains(component.GetType().Name): Mono's Type.Name builds a
        // fresh string per call, so one sweep over a blood-moon horde allocated
        // one string per component visited (thousands per sweep) and hashed it
        // by content. One lookup per distinct component TYPE, computed once
        // ever, replaces all of it. Bounded by the game's Behaviour type count
        // (a fixed set, not per entity), so this cannot grow with load.
        static readonly Dictionary<Type, bool> RigTypeByType = new Dictionary<Type, bool>();
        // Sweep scratch: the component list of the entity being walked, reused
        // across entities so the per-entity GetComponentsInChildren does not
        // allocate a fresh array for each one. Main-thread only (console
        // command), and consumed before the next entity is walked.
        static readonly List<Behaviour> Scratch = new List<Behaviour>();

        static bool IsRigType(Component component)
        {
            Type type = component.GetType();
            bool tracked;
            if (!RigTypeByType.TryGetValue(type, out tracked))
            {
                tracked = RigTypes.Contains(type.Name);
                RigTypeByType[type] = tracked;
            }
            return tracked;
        }

        /// <summary>
        /// Components disabled and not yet restored, across every sweep since the
        /// last <see cref="Exit"/>. Zero means nothing is armed.
        /// </summary>
        public static int Tracked { get { return _disabled.Count; } }

        // Unity's overloaded null comparison: a destroyed component reads == null
        // even though the reference is non-null.
        static bool IsDestroyed(Behaviour b) { return b == null; }

        /// <summary>
        /// Disable every still-enabled rig component of a tracked type under
        /// <paramref name="world"/>. Returns how many this sweep disabled; the
        /// cumulative count is <see cref="Tracked"/>.
        /// </summary>
        public static int Enter(World world)
        {
            PruneDespawnedTracked();
            int disabled = 0;
            List<Entity> entities = world.Entities.list;
            for (int i = 0; i < entities.Count; i++)
            {
                Scratch.Clear();
                entities[i].GetComponentsInChildren(true, Scratch);
                for (int b = 0; b < Scratch.Count; b++)
                {
                    if (Scratch[b] == null || !Scratch[b].enabled) continue;
                    if (!IsRigType(Scratch[b])) continue;
                    if (_disabledSet.Contains(Scratch[b])) continue;
                    Scratch[b].enabled = false;
                    _disabled.Add(Scratch[b]);
                    _disabledSet.Add(Scratch[b]);
                    disabled++;
                }
            }
            Scratch.Clear();
            return disabled;
        }

        /// <summary>
        /// Re-enable every tracked component and forget the tracking. Returns the
        /// number actually re-enabled (destroyed wrappers are skipped and still
        /// dropped, so the table cannot accumulate dead entries). A call with
        /// nothing tracked is a no-op, which is what makes a second `es rigon`
        /// and the config-driven release the same operation.
        /// </summary>
        public static int Exit()
        {
            int restored = 0;
            for (int i = 0; i < _disabled.Count; i++)
                if (_disabled[i] != null) { _disabled[i].enabled = true; restored++; }
            _disabled.Clear();
            _disabledSet.Clear();
            return restored;
        }

        // Tracked components whose entity died or despawned while disabled can
        // never be restored (only an unusable Unity wrapper remains), so keep
        // sweeping them would grow one entry per spawn/despawn for the whole bench
        // session. Drop them at every rigoff, the same prune-per-sweep contract as
        // AnimatorEmergency.PruneDespawnedSavedModes: alive entries stay tracked,
        // so a repeated rigoff still converges additive and one rigon undoes
        // everything still alive.
        static void PruneDespawnedTracked()
        {
            int removed = 0;
            for (int i = _disabled.Count - 1; i >= 0; i--)
            {
                if (IsDestroyed(_disabled[i]))
                {
                    _disabled.RemoveAt(i);
                    removed++;
                }
            }
            // The mirror is pruned by PREDICATE, never by Remove(destroyedRef):
            // the set compares keys through UnityEngine.Object.Equals, which
            // reports false for a destroyed instance against anything, so a
            // key-based removal could never match and the set would keep one dead
            // entry per despawned rig.
            if (removed > 0) _disabledSet.RemoveWhere(IsDestroyed);
            if (removed > 0)
                EsLog.Emit(LogLevel.Info, "rigprobe: pruned " + removed + " tracked component(s) whose "
                    + "rig despawned (tracked=" + _disabled.Count + ")");
        }
    }
}
