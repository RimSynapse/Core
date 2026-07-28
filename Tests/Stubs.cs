// The smallest real types that make SynapseCoreProviders compile, written from the signatures it
// actually meets. Not a mock framework: SynapseLogger captures instead of writing so the tests can
// assert on logging, which is half of what this surface promises.
using System;
using System.Collections.Generic;

namespace Verse
{
    /// <summary>Stands in for the RimWorld pawn. Identity is all the provider surface needs.</summary>
    public class Pawn
    {
        public string name;
        public Pawn(string name = "pawn") { this.name = name; }
    }
}

namespace RimSynapse
{
    /// <summary>
    /// Capturing stand-in for Core's logger. Same signatures as the real one
    /// (Source/SynapseLogger.cs), so a change there that breaks the call sites breaks this build.
    /// </summary>
    public static class SynapseLogger
    {
        public static readonly List<string> Messages = new List<string>();
        public static readonly List<string> Errors = new List<string>();

        public static void Message(string msg, string category = null, string modId = null)
        {
            Messages.Add(msg);
        }

        public static void Info(string msg, string category = null, string modId = null) => Message(msg, category, modId);
        public static void Warning(string msg, string category = null, string modId = null) => Message(msg, category, modId);
        public static void Warn(string msg, string category = null, string modId = null) => Message(msg, category, modId);

        public static void Error(string msg, string category = null, string modId = null)
        {
            Errors.Add(msg);
        }

        public static void Clear()
        {
            Messages.Clear();
            Errors.Clear();
        }

        /// <summary>How many captured messages contain <paramref name="fragment"/>.</summary>
        public static int CountContaining(string fragment)
        {
            int n = 0;
            foreach (var m in Messages) if (m.Contains(fragment)) n++;
            return n;
        }
    }

    /// <summary>
    /// Only the legacy delegate field matters here. The real type is a WorldComponent with a great
    /// deal of saved state; none of it is reachable from the provider surface.
    /// </summary>
    public partial class SynapseCoreWorldComponent
    {
        [Obsolete("Register SynapseCoreProviders.PopulationDensity instead.")]
        public static Func<int, int> GetPopulationDensityDelegate = null;
    }
}
