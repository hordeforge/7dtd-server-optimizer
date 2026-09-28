using System;

namespace EfficientServer.Patches
{
    /// <summary>
    /// The one place the mod answers "is this a dedicated server?", resolved once
    /// per process and read from every thread. Game-type-free, so the unit harness
    /// drives it directly, the same reason <see cref="TickClock"/>,
    /// <see cref="TickIntervalEma"/> and <see cref="GovernorTiers"/> live in their
    /// own files.
    ///
    /// Why it is not two plain fields with a volatile check: the previous shape
    /// (a volatile "resolved" flag beside a volatile value) let every thread that
    /// found the flag clear run the probe and WRITE the result. Two threads could
    /// interleave as
    ///
    ///   main          reads the flag clear, probes -> dedicated (the game has
    ///                 published IsDedicatedServer by now)
    ///   receive       reads the flag clear (the main thread has not stored it
    ///                 yet), probes -> not dedicated (it read the engine value
    ///                 before the game published it)
    ///   receive       stores not-dedicated
    ///   main          stores dedicated
    ///
    /// and the last store wins for the life of the process. The receive thread is
    /// a real caller: the LiteNetLib socket-receive thread runs the client-list
    /// snapshot's duplicate-IP scan, which calls the shared gate on every
    /// connection request. The window is exactly the boot one, where the flag is
    /// still clear, and the cost of losing it is the whole mod silently
    /// deactivating on a dedicated server: every patch prefix gates on this
    /// answer, and no log line says why.
    ///
    /// One volatile int carries all three states, so a reader cannot observe a
    /// half-written pair, and the lock makes the resolution first-writer-wins: the
    /// first thread to publish fixes the answer, and every later probe is
    /// discarded. The lock is only reachable while the state is Unresolved, so
    /// after the first answer no caller ever enters it.
    ///
    /// The probe runs OUTSIDE the lock and only its result is published under it.
    /// The probe is a read of the game's host-type flag, and the loser of the race
    /// is free to have made that read: what must not happen is a second publish,
    /// which the re-check under the lock already rules out. Nothing from the game
    /// is therefore ever called with this lock held.
    ///
    /// A probe that throws leaves the state Unresolved and the exception
    /// propagates: early in boot the game may not have published the host type
    /// yet, and the caller fails closed and retries on a later call rather than
    /// caching a wrong answer.
    /// </summary>
    internal sealed class DedicatedHostGate
    {
        /// <summary>No answer yet; the gate is closed until one is published.</summary>
        public const int Unresolved = -1;
        /// <summary>Resolved: a client host.</summary>
        public const int NotDedicated = 0;
        /// <summary>Resolved: a dedicated server.</summary>
        public const int Dedicated = 1;

        readonly object _gate = new object();
        volatile int _state = Unresolved;

        /// <summary>
        /// The published answer: <see cref="Unresolved"/>,
        /// <see cref="NotDedicated"/> or <see cref="Dedicated"/>. Volatile, so a
        /// reader on any thread sees the whole word with no torn state and with
        /// the publishing thread's earlier writes ordered before it.
        /// </summary>
        public int State { get { return _state; } }

        /// <summary>True only once a dedicated server has been confirmed.</summary>
        public bool IsDedicated { get { return _state == Dedicated; } }

        /// <summary>
        /// Publish the host type if no answer exists yet. Returns true for the
        /// call that published it, false for every later one (whose result is
        /// discarded), so no caller can overwrite the answer another thread
        /// already published.
        /// </summary>
        public bool Resolve(Func<bool> probe)
        {
            if (_state != Unresolved) return false;
            bool dedicated = probe();
            lock (_gate)
            {
                // Re-check inside the lock: without it two threads both see
                // Unresolved here and both publish, which is the race this class
                // exists to close.
                if (_state != Unresolved) return false;
                _state = dedicated ? Dedicated : NotDedicated;
                return true;
            }
        }
    }
}
