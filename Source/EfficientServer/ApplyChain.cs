using System;
using System.Collections.Generic;
using System.Text;

namespace EfficientServer
{
    /// <summary>
    /// One independent step of a start-time / reload apply chain, named so a
    /// failure can be reported without a stack trace.
    /// </summary>
    internal sealed class ApplyStep
    {
        public readonly string Name;
        public readonly Action Body;

        public ApplyStep(string name, Action body)
        {
            Name = name;
            Body = body;
        }
    }

    /// <summary>
    /// What one <see cref="ApplyChain"/> run did: how many steps ran clean, and the
    /// text describing every step that threw.
    /// </summary>
    internal sealed class ApplyChainResult
    {
        public int Applied;
        public int Failed;
        public readonly List<string> Failures = new List<string>();

        public bool AnyFailed { get { return Failed > 0; } }

        /// <summary>
        /// `name[Type]: message` pairs for the failed steps, in run order, pipe
        /// separated so a multi-step failure reads as one line in the log and one
        /// exception message at the console boundary.
        /// </summary>
        public string Summary()
        {
            if (!AnyFailed) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < Failures.Count; i++)
            {
                if (i > 0) sb.Append('|');
                sb.Append(Failures[i]);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Runs an apply chain with each step isolated from the others.
    ///
    /// The chains this replaces (<c>GameStartPatch.OnGameStartDone</c> and
    /// <c>ModApi.ReloadConfig</c>) wrapped every step in ONE try. That made a
    /// single throwing step a cascading failure: mesh budgets failing skipped the
    /// dedicated skips, target fps, job workers and GC incremental behind it, and
    /// the only thing the operator ever learned was "GameStartDone handler failed"
    /// plus a stack, with no list of which levers actually applied. The same held
    /// for a reload, where the chain rethrows into the console's refusal to print a
    /// success echo: the operator was told the reload failed, and half the levers it
    /// was supposed to re-apply silently never ran.
    ///
    /// Each step is a separate apply with its own boundary, so a failure in one
    /// lever cannot take the rest of the group down with it. The failures are
    /// COLLECTED, not swallowed: the caller reports them as one line naming the
    /// chain and every step that threw, so a partial apply is still loudly
    /// visible, and it can still rethrow when its contract demands a non-success
    /// signal.
    ///
    /// Game-type-free (no game symbols), so the unit harness covers it directly;
    /// see EfficientServer.Tests.csproj.
    /// </summary>
    internal static class ApplyChain
    {
        public static ApplyChainResult Run(IList<ApplyStep> steps)
        {
            var result = new ApplyChainResult();
            for (int i = 0; i < steps.Count; i++)
            {
                ApplyStep step = steps[i];
                try
                {
                    step.Body();
                    result.Applied++;
                }
                catch (Exception ex)
                {
                    // Type + message, not the full exception: the chain reports
                    // which step broke by name, so the stack adds noise without
                    // adding identity. The inner exception is folded into the
                    // message the runtime already produced.
                    result.Failed++;
                    result.Failures.Add(step.Name + "[" + ex.GetType().Name + "]: " + ex.Message);
                }
            }
            return result;
        }
    }
}
