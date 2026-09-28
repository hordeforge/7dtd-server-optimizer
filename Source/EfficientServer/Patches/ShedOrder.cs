using System.Collections.Generic;

namespace EfficientServer.Patches
{
    /// <summary>
    /// Which enemies a tick-guard shed batch removes, as a pure function of the
    /// living-enemy census (<c>(distSq, entityId)</c> per enemy, distSq = distance
    /// to the nearest player). Game-type-free so the unit harness can drive it
    /// directly, the same reason <see cref="TickClock"/>,
    /// <see cref="TickIntervalEma"/> and <see cref="GovernorTiers"/> live in their
    /// own files.
    ///
    /// Distance alone is NOT a total order: entities that share a position share a
    /// distSq exactly, and the census arrives in <c>World.Entities.list</c> order,
    /// which carries no meaning the mod controls. A batch boundary that falls
    /// inside such a tie group would then cut it by list order, so the same world
    /// at the same distances could shed two different zombies on two runs. The id
    /// tie-break makes the order total, so a replayed run sheds the same ids in
    /// the same sequence.
    /// </summary>
    internal static class ShedOrder
    {
        /// <summary>
        /// The <paramref name="shed"/> ids to remove, farthest from a player first
        /// and lowest id first inside a distance tie. Clamped to the census size,
        /// so a caller passing a batch larger than the horde gets the whole horde.
        /// Does not mutate <paramref name="census"/>.
        /// </summary>
        public static List<int> Select(List<(float distSq, int entityId)> census, int shed)
        {
            var ordered = new List<(float distSq, int entityId)>(census);
            // Lambda, not a method group: mcs will not convert a method with
            // ValueTuple parameters to Comparison<T>.
            ordered.Sort((a, b) => FartherFirst(a, b));
            int count = shed < ordered.Count ? shed : ordered.Count;
            if (count < 0) count = 0;
            var shedIds = new List<int>(count);
            for (int i = 0; i < count; i++)
                shedIds.Add(ordered[i].entityId);
            return shedIds;
        }

        /// <summary>
        /// The one ordering: descending distance, then ascending id. A NaN
        /// distance (a corrupt position, where CompareTo reports NaN as less than
        /// everything in one argument order and more in the other, which would
        /// make the comparison non-transitive and hand the batch boundary back to
        /// enumeration order) counts as the farthest and falls in with the rest
        /// on the id. Parameters are unnamed because the tuple element names carry
        /// no meaning to a comparison.
        /// </summary>
        static int FartherFirst((float, int) a, (float, int) b)
        {
            bool aBad = float.IsNaN(a.Item1);
            bool bBad = float.IsNaN(b.Item1);
            if (aBad != bBad) return aBad ? -1 : 1;
            if (!aBad)
            {
                int byDistance = b.Item1.CompareTo(a.Item1);
                if (byDistance != 0) return byDistance;
            }
            return a.Item2.CompareTo(b.Item2);
        }
    }
}
