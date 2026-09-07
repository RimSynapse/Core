using System;
using System.Collections.Generic;
using Verse;

namespace RimSynapse
{
    /// <summary>
    /// The letter-enhancement whitelist (Core #134). RimSynapse holds and enhances a letter (LLM
    /// rewrite, storyteller TTS) ONLY when it matches a registered hook; every other letter passes
    /// straight through to vanilla handling and arrives on time.
    ///
    /// This inverts the old blanket "hold every threat and quest" gate — the reason a crash-landed
    /// pod's rescue alert used to arrive after the downed pawn had bled out. The default is now
    /// vanilla; enhancement is strictly opt-in. If there is no hook for an event, it is not held.
    ///
    /// Register a letter by def name (reflection-friendly: a plain string, no Core type in the
    /// signature, so a producer can register without referencing Core) or by a predicate for richer
    /// matching — a specific quest condition, or a forecast/prediction hook (#58).
    /// </summary>
    public static class SynapseLetterEnhancement
    {
        private static readonly HashSet<string> defNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<Func<Letter, bool>> predicates = new List<Func<Letter, bool>>();
        private static readonly object gate = new object();

        /// <summary>Whitelist a letter def by name (e.g. "ThreatBig"). Idempotent.</summary>
        public static void RegisterLetterDef(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return;
            lock (gate) { defNames.Add(defName); }
        }

        /// <summary>Whitelist letters matching a predicate, for conditions a def name can't express.
        /// The predicate must be cheap and must not throw — a throwing hook is caught and treated as
        /// no-match, never as a reason to hold.</summary>
        public static void RegisterPredicate(Func<Letter, bool> predicate)
        {
            if (predicate == null) return;
            lock (gate) { predicates.Add(predicate); }
        }

        /// <summary>True when <paramref name="letter"/> is whitelisted for hold + enhancement. No match
        /// means vanilla handling (fire immediately) — the failure-safe default.</summary>
        public static bool ShouldEnhance(Letter letter)
        {
            if (letter == null) return false;
            lock (gate)
            {
                if (letter.def != null && defNames.Contains(letter.def.defName)) return true;
                foreach (var p in predicates)
                {
                    try { if (p(letter)) return true; }
                    catch { /* a bad hook must never force a hold */ }
                }
            }
            return false;
        }

        /// <summary>Snapshot of the registered def names, for debug/inspection.</summary>
        public static List<string> RegisteredDefNames()
        {
            lock (gate) { return new List<string>(defNames); }
        }

        /// <summary>Count of registered predicate hooks, for debug/inspection.</summary>
        public static int PredicateCount
        {
            get { lock (gate) { return predicates.Count; } }
        }
    }
}
