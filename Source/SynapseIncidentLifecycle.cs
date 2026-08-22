using System;
using System.Collections.Generic;

namespace RimSynapse
{
    /// <summary>
    /// Broadcast hook for the lifecycle of regionalizable incidents (Core #64): Core announces when
    /// a tracked incident <b>starts</b> and when it reaches <b>first-level resolution</b>, and any
    /// consumer (Storyteller, WorldNews, Factions, Regions) reacts on its own terms.
    ///
    /// Follows Core's broadcast-hook shape: many subscribers, pushed, and — crucially — the event
    /// payloads are <b>primitives only</b>, so a consumer subscribes by reflection with NO Core type
    /// in the signature and therefore builds and runs with Core absent:
    /// <code>
    ///   var t = GenTypes.GetTypeInAnyAssembly("RimSynapse.SynapseIncidentLifecycle");
    ///   t.GetEvent("OnIncidentStarted").AddEventHandler(null,
    ///       (Action&lt;string,string,float,string,int&gt;)MyStartHandler);
    /// </code>
    ///
    /// Regionalizable = the environmental Tier A–D incidents (solar flare, toxic fallout, disease,
    /// weather, psychic). Colony-local incidents (raids, infestations) do not emit a start; they may
    /// still emit a resolution beat where one is meaningful. Classification and the resolution dedup
    /// are game-free so the Tier-1 sandbox can pin them; the Harmony emit points supply the strings.
    /// </summary>
    public static class SynapseIncidentLifecycle
    {
        /// <summary>
        /// A regionalizable incident started. Payload: (kind, region, magnitude, origin, leadTimeTicks).
        /// <paramref name="origin"/> may be empty; <paramref name="leadTimeTicks"/> is 0 for an
        /// immediate onset.
        /// </summary>
        public static event Action<string, string, float, string, int> OnIncidentStarted;

        /// <summary>
        /// A tracked incident reached first-level resolution. Payload: (kind, region, outcome).
        /// </summary>
        public static event Action<string, string, string> OnIncidentResolved;

        // Resolution dedup: a given resolution key fires at most once, so an oscillating condition
        // (ending and being re-registered, or End() called more than once) never double-emits.
        // Bounded so a very long game cannot grow it without limit.
        private static readonly HashSet<string> _resolvedKeys = new HashSet<string>();
        private static readonly Queue<string> _resolvedOrder = new Queue<string>();
        private const int MaxResolvedKeys = 512;

        // ── Classification (game-free) ──

        private static readonly HashSet<string> RegionalizableDefs =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Solar / psychic / sky
            "SolarFlare", "Eclipse", "Aurora",
            "PsychicDrone", "PsychicSoothe", "PsychicSuppression", "PsychicEmanatorShipPartCrash",
            // Toxic / volcanic / haze
            "ToxicFallout", "VolcanicWinter", "NoxiousHaze",
            // Weather / temperature
            "Flashstorm", "HeatWave", "ColdSnap", "UnnaturalDarkness",
        };

        private static readonly HashSet<string> RegionalizableCategories =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DiseaseHuman", "DiseaseAnimal",
        };

        /// <summary>
        /// Whether an incident (by def name and category) is a regionalizable Tier A–D emitter.
        /// Pure — the Harmony emit point passes the def/category strings.
        /// </summary>
        public static bool IsRegionalizable(string defName, string categoryDefName)
        {
            if (!string.IsNullOrEmpty(defName) && RegionalizableDefs.Contains(defName)) return true;
            if (!string.IsNullOrEmpty(categoryDefName) && RegionalizableCategories.Contains(categoryDefName)) return true;
            return false;
        }

        // ── Broadcast ──

        /// <summary>
        /// Announce a start. Contained so a throwing subscriber cannot take the firing path down.
        /// </summary>
        public static void BroadcastStarted(string kind, string region, float magnitude, string origin, int leadTimeTicks)
        {
            var handlers = OnIncidentStarted;
            if (handlers == null) return;
            foreach (var h in handlers.GetInvocationList())
            {
                try { ((Action<string, string, float, string, int>)h)(kind, region, magnitude, origin ?? "", leadTimeTicks); }
                catch (Exception ex) { SafeLog($"OnIncidentStarted subscriber threw: {ex.Message}"); }
            }
        }

        /// <summary>
        /// Announce a first-level resolution, deduped by <paramref name="dedupKey"/>. Returns false
        /// (and emits nothing) if this key already resolved — the anti-oscillation guarantee. A
        /// null/empty key always emits (caller opted out of dedup).
        /// </summary>
        public static bool BroadcastResolved(string kind, string region, string outcome, string dedupKey)
        {
            if (!string.IsNullOrEmpty(dedupKey) && !MarkResolved(dedupKey)) return false;

            var handlers = OnIncidentResolved;
            if (handlers == null) return true; // deduped/accepted even with no subscribers
            foreach (var h in handlers.GetInvocationList())
            {
                try { ((Action<string, string, string>)h)(kind, region, outcome ?? ""); }
                catch (Exception ex) { SafeLog($"OnIncidentResolved subscriber threw: {ex.Message}"); }
            }
            return true;
        }

        /// <summary>Record a resolution key; false if it was already recorded (dedup hit).</summary>
        private static bool MarkResolved(string dedupKey)
        {
            if (_resolvedKeys.Contains(dedupKey)) return false;
            _resolvedKeys.Add(dedupKey);
            _resolvedOrder.Enqueue(dedupKey);
            while (_resolvedOrder.Count > MaxResolvedKeys)
                _resolvedKeys.Remove(_resolvedOrder.Dequeue());
            return true;
        }

        /// <summary>Test seam: forget all recorded resolution keys.</summary>
        public static void ResetResolvedForTest() { _resolvedKeys.Clear(); _resolvedOrder.Clear(); }

        /// <summary>The most recent subscriber exception message (for the debug action). Null if none.</summary>
        public static string LastSubscriberError { get; private set; }

        // Kept game-free (no Verse/SynapseLogger reference) so the Tier-1 sandbox can compile this
        // class alone: a throwing subscriber is contained and its message recorded, not logged here.
        private static void SafeLog(string msg) => LastSubscriberError = msg;
    }
}
