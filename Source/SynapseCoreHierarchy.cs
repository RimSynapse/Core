using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// An OPEN-ENDED leadership / "reports-to" hierarchy hook (Psychology #21, for RimSynapse-Factions).
    /// Core owns no org chart of its own — it just exposes the seam. A mod that actually knows the structure
    /// (Factions) registers a single <see cref="Provider"/>; until then every query is null/empty, so the hook
    /// is present but dormant and nothing changes.
    ///
    /// Nodes are deliberately untyped (<c>object</c>) so the same hierarchy can span whatever a mod wants to
    /// hang off it — a leader <see cref="Pawn"/> today, and later a vanilla Settlement / WorldObject or any
    /// other thing — without Core taking a dependency on those types. The typed <c>*Pawn</c> helpers are the
    /// built-out surface consumers use now; the object surface is the framework left open for the rest.
    /// </summary>
    public static class SynapseCoreHierarchy
    {
        /// <summary>Implemented by the mod that owns the structure (Factions). All members may return null/empty
        /// for nodes it doesn't manage — a settlement node it hasn't wired yet simply answers nothing.</summary>
        public interface IHierarchyProvider
        {
            /// <summary>The node's immediate superior (a settlement leader → the faction leader), or null.</summary>
            object ReportsTo(object node);
            /// <summary>The nodes that report directly to this one, or empty.</summary>
            IEnumerable<object> DirectReports(object node);
            /// <summary>A human-readable role for this node ("Faction Leader", "Leader of <settlement>"), or null.</summary>
            string RoleTitle(object node);
        }

        /// <summary>The single registered provider, or null when no mod supplies a hierarchy (the default —
        /// the hook stays dormant). A mod sets this once at startup.</summary>
        public static IHierarchyProvider Provider;

        /// <summary>True when some mod has registered a hierarchy to query.</summary>
        public static bool HasProvider => Provider != null;

        // --- Open-ended surface: a node is any object (a Pawn now; a Settlement / WorldObject / anything later) ---

        public static object ReportsTo(object node) => node == null ? null : Provider?.ReportsTo(node);

        public static IEnumerable<object> DirectReports(object node)
            => (node == null ? null : Provider?.DirectReports(node)) ?? Enumerable.Empty<object>();

        public static string RoleTitle(object node) => node == null ? null : Provider?.RoleTitle(node);

        /// <summary>Walk <see cref="ReportsTo"/> from this node up to the top, most-immediate superior first.
        /// Cycle-guarded, so a malformed provider can never loop forever.</summary>
        public static IReadOnlyList<object> ChainOfCommand(object node)
        {
            var chain = new List<object>();
            var seen = new HashSet<object>();
            var cur = ReportsTo(node);
            while (cur != null && seen.Add(cur))
            {
                chain.Add(cur);
                cur = ReportsTo(cur);
            }
            return chain;
        }

        // --- Pawn-centric convenience (typed surface for today's leader-pawn use) ---

        public static Pawn ReportsToPawn(Pawn pawn) => ReportsTo(pawn) as Pawn;

        public static IEnumerable<Pawn> DirectReportPawns(Pawn pawn) => DirectReports(pawn).OfType<Pawn>();

        public static IReadOnlyList<Pawn> ChainOfCommandPawns(Pawn pawn) => ChainOfCommand(pawn).OfType<Pawn>().ToList();
    }
}
