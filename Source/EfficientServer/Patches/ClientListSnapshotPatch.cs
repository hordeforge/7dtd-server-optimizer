using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Stock join-churn race fix (root cause closed in engine RE, network.md 4.0).
    /// <c>LiteNetLibAuthWrapperServer.ConnectionRequestCheck</c> - the connection-request
    /// handler that rate-limits by IP, rejects duplicate in-flight IPs, checks the
    /// server password, and accepts - runs ON THE LITENETLIB SOCKET-RECEIVE THREAD,
    /// because NetworkCommonLiteNetLib.InitConfig sets UnsyncedEvents=true and the
    /// library then dispatches events inline instead of queuing them for PollEvents.
    /// Its duplicate-IP check enumerates ConnectionManager.Clients.List directly, the
    /// SAME live List the MAIN thread mutates on every join/disconnect. Under join
    /// churn the version-checked enumerator throws InvalidOperationException
    /// ("Collection was modified") on the receive thread; the exception escapes
    /// CreateEvent, drops the packets being processed, and cascades into
    /// RemoteConnectionClose for connected clients (measured: 302 close bursts over
    /// 4 minutes at 16-28 bot churn on stock V3.1.0).
    ///
    /// This transpiler reroutes ONLY the enumerator acquisition through
    /// <see cref="DuplicateScanSource"/>, which hands back an enumerator over a
    /// private snapshot built via ICollection.CopyTo. CopyTo does not version-check
    /// and arrays are fixed-size, so the enumeration cannot throw regardless of what
    /// the main thread does concurrently: worst cases are a bounded-staleness answer
    /// (a client added mid-copy is missed once) or, if the copy itself races a resize,
    /// a caught ArgumentException -> empty snapshot -> the duplicate check passes and
    /// the request proceeds to password/accept exactly as when no client matches.
    /// Rate limiting, the pending-IP and password rejects, and Accept all stay on the
    /// receive thread untouched, so no wrapper state changes threads.
    ///
    /// Thread-safety contract (ARCHITECTURE concurrency model): this helper executes
    /// on the receive thread and touches shared state only through the sanctioned
    /// cross-thread read set - the published ModApi.Config reference and
    /// ModApi.Active, ShouldRun's dedicated-flag publication, and the CopyTo
    /// snapshot. It holds no lock and mutates nothing another thread can observe.
    ///
    /// Targets a method resolved BY NAME in Assembly-CSharp (the wrapper type is not
    /// referenced at compile time), so drift fails visibly: a moved type surfaces as
    /// MISSING TARGET at init, a changed body as the transpiler's thrown
    /// InvalidOperationException. Default ON: it preserves the vanilla decision
    /// semantics up to one copy instant and removes a crash; the opt-out restores the
    /// exact vanilla enumerator.
    /// </summary>
    [HarmonyPatch]
    internal static class ClientListSnapshotPatch
    {
        static MethodBase TargetMethod()
        {
            // Nested type: CLR name uses '+', not '.'. The dot form silently
            // never resolves (Harmony's TypeByName does not convert '.' to '+'),
            // which surfaced as MISSING TARGET on every build including stock
            // 3.1.0. Verified nested structure from engine-research IL dumps
            // (3.1.0 and 3.2.0 identical): NetworkServerLiteNetLib+LiteNetLibAuthWrapperServer.
            Type wrapper = AccessTools.TypeByName(
                "NetworkServerLiteNetLib+LiteNetLibAuthWrapperServer");
            return wrapper != null ? AccessTools.Method(wrapper, "ConnectionRequestCheck") : null;
        }

        static bool Prepare() => TargetMethod() != null;

        // Registry key for the snapshot's fail-open catch; see <see cref="Degrade"/>.
        // The catch fires on the receive thread for every connection request, so
        // the log line is one-per-process, while the registry keeps counting every
        // fallback and `es status` shows the total: a single occurrence and a
        // persistent copy failure under join churn are the same one log line.
        internal const string DegradeKey = "clientListSnapshot";

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo source = AccessTools.Method(
                typeof(ClientListSnapshotPatch), nameof(DuplicateScanSource));
            int swapped = 0;
            foreach (CodeInstruction ins in instructions)
            {
                // The sole Clients.List enumerator acquisition in the method (IL_007d
                // in V3.1.0): stack [.., ReadOnlyCollection<ClientInfo>] ->
                // IEnumerator<ClientInfo>. The replacement consumes and produces the
                // same types, so no other instruction moves.
                if (swapped == 0 && IsClientsListEnumerator(ins))
                {
                    swapped++;
                    yield return new CodeInstruction(OpCodes.Call, source)
                    { labels = ins.labels, blocks = ins.blocks };
                }
                else
                {
                    yield return ins;
                }
            }
            EsLog.Emit(LogLevel.Info, "ClientListSnapshotPatch: rerouted "
                + swapped + " duplicate-IP client-list scan(s)");
            // Matched-but-untransformed would silently leave the stock race in place;
            // fail loudly so target drift surfaces as a visible init error.
            if (swapped == 0)
                throw new InvalidOperationException(
                    "ClientListSnapshotPatch: Clients.List GetEnumerator not found in "
                    + "ConnectionRequestCheck; target drifted - patch inactive.");
        }

        static bool IsClientsListEnumerator(CodeInstruction ins)
        {
            if (!(ins.opcode == OpCodes.Callvirt || ins.opcode == OpCodes.Call))
                return false;
            if (!(ins.operand is MethodInfo m) || m.Name != "GetEnumerator")
                return false;
            Type decl = m.DeclaringType;
            return decl != null && decl.IsGenericType
                && decl.GetGenericTypeDefinition() == typeof(ReadOnlyCollection<>);
        }

        // Replaces the vanilla GetEnumerator call. Runs on the RECEIVE THREAD (see
        // class comment). Returns the stock live enumerator when the lever is off so
        // the opt-out behaves exactly like vanilla.
        public static IEnumerator<ClientInfo> DuplicateScanSource(ReadOnlyCollection<ClientInfo> live)
        {
            if (live == null)
                return Generic(Empty());
            NetworkConfig cfg = ModApi.Config != null ? ModApi.Config.Network : null;
            if (!ModApi.ShouldRun() || cfg == null || !cfg.ClientListSnapshot)
                return Generic(live);

            ClientInfo[] raw;
            try
            {
                // CopyTo-based snapshot: no version check, no enumerator exception.
                // A concurrent grow past the captured Count makes List.CopyTo argue
                // about lengths (caught below -> empty scan, fail open); shrink or
                // steady state yield a valid point-in-time copy.
                raw = new ClientInfo[live.Count];
                ((ICollection<ClientInfo>)live).CopyTo(raw, 0);
            }
            catch (Exception ex)
            {
                // Fail-open is the documented outcome, but never SILENT: a
                // snapshot that fell back to an empty scan lets a duplicate-IP
                // join through, and the operator reading the log has no other
                // way to learn the guard degraded. One line per process (this
                // runs on the receive thread for every connection request, so a
                // per-hit line would flood the log under join churn), naming the
                // failure so the cause is greppable. Degrade.Report both
                // announces-once and counts every hit, so the rate is readable
                // from `es status` instead of inferred from one line.
                if (Degrade.Report(DegradeKey, "client-list snapshot failed [" + ex.GetType().Name
                        + "]: " + ex.Message + " - duplicate-IP check falls open for that "
                        + "request (announced once; per-hit count in 'es status')"))
                    EsLog.Emit(LogLevel.Warn, Degrade.FirstReport(DegradeKey));
                return Generic(Empty());
            }
            // Belt and suspenders across host BCL variations: drop any torn tail slot
            // (a RemoveAt clearing the vacated last element) so the consumer's
            // loginDone/ip derefs can never see null.
            int holes = 0;
            for (int i = 0; i < raw.Length; i++)
                if (raw[i] == null) holes++;
            if (holes > 0)
            {
                var clean = new ClientInfo[raw.Length - holes];
                for (int i = 0, k = 0; i < raw.Length; i++)
                    if (raw[i] != null) clean[k++] = raw[i];
                raw = clean;
            }
            return Generic(raw);
        }

        // Direct GetEnumerator, not Enumerable.Cast: the transpiler swaps the
        // game's enumerator acquisition for a call to this, and the injected call
        // site stays inside a per-connection hot path where Cast would wrap every
        // element in a second enumerator.
        static IEnumerator<ClientInfo> Generic(IEnumerable<ClientInfo> source) =>
            ((IEnumerable<ClientInfo>)source).GetEnumerator();

        static ClientInfo[] Empty() => new ClientInfo[0];
    }
}
