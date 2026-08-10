using Verse;

namespace RimSynapse.Models
{
    /// <summary>How weighty an event is — drives coalescing (trivial repeats roll up, serious stay
    /// individual), the significance floor, and dynamic memory length (Core#88).</summary>
    public enum EventSeverity { Trivial, Standard, Serious, Major }

    public class PastEvent : IExposable
    {
        public string eventId;
        public string parentEventId;
        public int gameTick;
        public SynapseDate date;
        public string eventDescription;
        public string mcpTag;
        public string category;
        public string factionName;
        public string settlementName;

        // Episode coalescing (Core#88): a single ordeal (e.g. 27 crow claws) becomes ONE episode that
        // accretes until it settles. severity drives rollup-vs-individual; the pawn-id tiers drive
        // per-pawn, involvement-gated narration (involved = first-hand, witness = saw it, afterEffect =
        // dealt with the consequences, e.g. the doctor who tended the wounded).
        public EventSeverity severity = EventSeverity.Standard;
        public int occurrenceCount = 1;
        public int firstTick;
        public int lastUpdateTick;
        public System.Collections.Generic.List<string> involvedPawnIds = new System.Collections.Generic.List<string>();
        public System.Collections.Generic.List<string> witnessPawnIds = new System.Collections.Generic.List<string>();
        public System.Collections.Generic.List<string> afterEffectPawnIds = new System.Collections.Generic.List<string>();

        /// <summary>Stable per-episode identity used to coalesce repeats of the same ordeal.</summary>
        public string CoalesceKey => !string.IsNullOrEmpty(mcpTag) ? mcpTag : (category ?? "event");

        public string outcomeDescription;
        public EventOutcome outcome = EventOutcome.Unknown;
        public bool isResolved;
        public int resolvedTick;

        public float startWealth;
        public float endWealth;
        public float startFoodNutrition;
        public float endFoodNutrition;

        public string sourceFactionId;
        public string targetFactionId;

        public string colonySnapshot;
        public System.Collections.Generic.Dictionary<string, string> pawnSnapshots = new System.Collections.Generic.Dictionary<string, string>();

        public PastEvent()
        {
            eventId = System.Guid.NewGuid().ToString();
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref eventId, "eventId");
            Scribe_Values.Look(ref parentEventId, "parentEventId");
            Scribe_Values.Look(ref gameTick, "gameTick");
            Scribe_Deep.Look(ref date, "date");
            Scribe_Values.Look(ref eventDescription, "eventDescription");
            Scribe_Values.Look(ref mcpTag, "mcpTag");
            Scribe_Values.Look(ref category, "category");
            Scribe_Values.Look(ref factionName, "factionName");
            Scribe_Values.Look(ref settlementName, "settlementName");
            Scribe_Values.Look(ref outcomeDescription, "outcomeDescription");
            Scribe_Values.Look(ref outcome, "outcome", EventOutcome.Unknown);
            Scribe_Values.Look(ref isResolved, "isResolved");
            Scribe_Values.Look(ref resolvedTick, "resolvedTick", 0);
            Scribe_Values.Look(ref startWealth, "startWealth", 0f);
            Scribe_Values.Look(ref endWealth, "endWealth", 0f);
            Scribe_Values.Look(ref startFoodNutrition, "startFoodNutrition", 0f);
            Scribe_Values.Look(ref endFoodNutrition, "endFoodNutrition", 0f);
            Scribe_Values.Look(ref sourceFactionId, "sourceFactionId");
            Scribe_Values.Look(ref targetFactionId, "targetFactionId");
            Scribe_Values.Look(ref colonySnapshot, "colonySnapshot");
            Scribe_Collections.Look(ref pawnSnapshots, "pawnSnapshots", LookMode.Value, LookMode.Value);

            Scribe_Values.Look(ref severity, "severity", EventSeverity.Standard);
            Scribe_Values.Look(ref occurrenceCount, "occurrenceCount", 1);
            Scribe_Values.Look(ref firstTick, "firstTick", 0);
            Scribe_Values.Look(ref lastUpdateTick, "lastUpdateTick", 0);
            Scribe_Collections.Look(ref involvedPawnIds, "involvedPawnIds", LookMode.Value);
            Scribe_Collections.Look(ref witnessPawnIds, "witnessPawnIds", LookMode.Value);
            Scribe_Collections.Look(ref afterEffectPawnIds, "afterEffectPawnIds", LookMode.Value);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (pawnSnapshots == null) pawnSnapshots = new System.Collections.Generic.Dictionary<string, string>();
                if (string.IsNullOrEmpty(eventId)) eventId = System.Guid.NewGuid().ToString();
                if (involvedPawnIds == null) involvedPawnIds = new System.Collections.Generic.List<string>();
                if (witnessPawnIds == null) witnessPawnIds = new System.Collections.Generic.List<string>();
                if (afterEffectPawnIds == null) afterEffectPawnIds = new System.Collections.Generic.List<string>();
            }

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                EnsureMcpTag();
            }
        }

        public void EnsureMcpTag()
        {
            if (!string.IsNullOrEmpty(mcpTag)) return;

            string baseTag = "UnknownEvent";
            string descLower = eventDescription?.ToLowerInvariant() ?? "";
            if (category == "ThreatBig")
            {
                if (descLower.Contains("raid")) baseTag = "HostileRaid";
                else if (descLower.Contains("infestation")) baseTag = "Infestation";
                else if (descLower.Contains("manhunter")) baseTag = "ManhunterPack";
                else if (descLower.Contains("fallout")) baseTag = "ToxicFallout";
                else if (descLower.Contains("cold snap")) baseTag = "ColdSnap";
                else if (descLower.Contains("heat wave")) baseTag = "HeatWave";
                else baseTag = "MajorThreat";
            }
            else if (category == "ThreatSmall")
            {
                if (descLower.Contains("mad")) baseTag = "MadAnimal";
                else if (descLower.Contains("short circuit") || descLower.Contains("zztt")) baseTag = "ShortCircuit";
                else if (descLower.Contains("blight")) baseTag = "CropBlight";
                else baseTag = "MinorThreat";
            }
            else if (category == "FactionArrival")
            {
                if (descLower.Contains("trader") || descLower.Contains("caravan")) baseTag = "TradeCaravan";
                else if (descLower.Contains("visitor") || descLower.Contains("guest")) baseTag = "PeacefulVisitor";
                else if (descLower.Contains("tribute")) baseTag = "TributeCollector";
                else baseTag = "FactionArrival";
            }
            else if (category == "Misc")
            {
                if (descLower.Contains("join") || descLower.Contains("wanderer")) baseTag = "PawnJoin";
                else if (descLower.Contains("quest")) baseTag = "QuestEvent";
                else if (descLower.Contains("cargo") || descLower.Contains("pod")) baseTag = "CargoPods";
                else if (descLower.Contains("art") || descLower.Contains("legendary")) baseTag = "LegendaryArtCreated";
                else baseTag = "MiscEvent";
            }
            else if (category == "DiseaseHuman")
            {
                baseTag = "HumanDisease";
            }
            else if (category == "DiseaseAnimal")
            {
                baseTag = "AnimalDisease";
            }
            else if (!string.IsNullOrEmpty(category))
            {
                baseTag = category;
            }

            // Extract subject name (pawn name) if any player colonist name is mentioned in the description
            string subject = null;
            try
            {
                if (Current.ProgramState == ProgramState.Playing)
                {
                    var colonists = RimWorld.PawnsFinder.AllMaps_FreeColonists;
                    if (colonists != null)
                    {
                        foreach (var p in colonists)
                        {
                            string name = p.Name?.ToStringShort;
                            if (!string.IsNullOrEmpty(name) && descLower.Contains(name.ToLowerInvariant()))
                            {
                                subject = name;
                                break;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Safe fallback if called during early loading phases
            }

            string details = "";
            if (!string.IsNullOrEmpty(subject))
            {
                details += subject;
            }
            if (!string.IsNullOrEmpty(factionName))
            {
                if (details.Length > 0) details += ", ";
                details += factionName;
            }

            if (details.Length > 0)
            {
                mcpTag = $"{baseTag}({details})";
            }
            else
            {
                mcpTag = baseTag;
            }
        }
    }
}
