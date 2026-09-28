using System;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Shared "never throttle this entity" probe used by BOTH the updateTasks LOD
    /// (<see cref="UpdateTasksLodPatch"/>) and path admission
    /// (<see cref="PathAdmissionPatch"/>): hunting, investigating, recently
    /// alerted, or non-passive-sleeper entities always run at full rate and always
    /// admit their paths. One copy so the two levers cannot drift apart on who
    /// counts as combat-priority. Probe failure is API drift; callers fail OPEN
    /// (everything counts as alerted -> throttling inactive), announced once
    /// because this fires per entity per tick, and the degradation stays visible
    /// in <c>es status</c> as <c>aiAlertProbe</c> until restart.
    /// </summary>
    internal static class AiAlertGate
    {
        /// <summary>Registry key for this fail-open; see <see cref="Degrade"/>.</summary>
        internal const string DegradeKey = "aiAlertProbe";

        public static bool IsAlertedOrBusy(EntityAlive entity)
        {
            try
            {
                if (entity.GetAttackTarget() != null) return true;
                if (entity.HasInvestigatePosition) return true;
                if (entity.GetAlertTicks() > 0) return true;
                if (entity.IsSleeper && !entity.IsSleeperPassive) return true;
                return false;
            }
            catch (Exception ex)
            {
                if (Degrade.Report(DegradeKey, "AI alert check failed [" + ex.GetType().Name + "]: " + ex.Message
                        + " - every entity now counts as alerted; AI LOD striding/skips and"
                        + " path admission are INACTIVE until restart"))
                    EsLog.Emit(LogLevel.Warn, Degrade.FirstReport(DegradeKey));
                return true; // API drift -> fail open rather than break AI
            }
        }
    }
}
