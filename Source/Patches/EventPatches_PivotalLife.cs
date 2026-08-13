using HarmonyLib;
using RimWorld;
using Verse;

namespace RimSynapse.Patches
{
    /// <summary>
    /// Pivotal life-event hooks (Core #92): imprisonment (arrest/capture), release (freed),
    /// ideoligion conversion, and prison-break attempts each secure a long-term memory via
    /// <see cref="SynapsePivotalMemory"/>. Recruitment/enslavement are handled in
    /// <c>EventPatches_Social</c> (the existing SetFaction patch). All are idempotent per pawn+class,
    /// so a double-fire (or a redundant engine call) never duplicates the memory.
    /// </summary>
    internal static class PivotalLifeEventHelpers
    {
        internal static string Name(Pawn p) => p.Name?.ToStringShort ?? p.KindLabel ?? "Someone";
    }

    /// <summary>Snapshot of a pawn's captivity state taken before <c>SetGuestStatus</c> runs.</summary>
    internal struct PivotalGuestState
    {
        public bool wasPrisoner;
        public bool wasSlave;
        public bool wasFreeColonist;
    }

    // Arrest / capture (became a prisoner) and freed (left prison/slavery).
    [HarmonyPatch(typeof(Pawn_GuestTracker), "SetGuestStatus")]
    internal static class Patch_GuestTracker_SetGuestStatus_Pivotal
    {
        static void Prefix(Pawn ___pawn, out PivotalGuestState __state)
        {
            __state = default;
            if (___pawn == null) return;
            __state.wasPrisoner = ___pawn.IsPrisoner;
            __state.wasSlave = ___pawn.IsSlave;
            __state.wasFreeColonist = ___pawn.IsColonist && !___pawn.IsPrisoner && !___pawn.IsSlave;
        }

        static void Postfix(Pawn ___pawn, PivotalGuestState __state)
        {
            if (___pawn == null || Current.ProgramState != ProgramState.Playing) return;

            bool nowPrisoner = ___pawn.IsPrisoner;
            bool nowSlave = ___pawn.IsSlave;

            // Became a prisoner of the colony: arrested (a free colonist) vs captured (anyone else).
            if (!__state.wasPrisoner && nowPrisoner)
            {
                if (__state.wasFreeColonist)
                    SynapsePivotalMemory.Record(___pawn, SynapsePivotalMemory.Arrested,
                        $"{PivotalLifeEventHelpers.Name(___pawn)} was arrested and imprisoned.");
                else
                    SynapsePivotalMemory.Record(___pawn, SynapsePivotalMemory.Captured,
                        $"{PivotalLifeEventHelpers.Name(___pawn)} was captured and imprisoned.");
            }
            // Left imprisonment or slavery for freedom.
            else if ((__state.wasPrisoner || __state.wasSlave) && !nowPrisoner && !nowSlave)
            {
                SynapsePivotalMemory.Record(___pawn, SynapsePivotalMemory.Freed,
                    $"{PivotalLifeEventHelpers.Name(___pawn)} was freed.");
            }
        }
    }

    // Ideoligion conversion (Ideology DLC). Records only a genuine change on a player colonist.
    [HarmonyPatch(typeof(Pawn_IdeoTracker), "SetIdeo")]
    internal static class Patch_IdeoTracker_SetIdeo_Pivotal
    {
        static void Prefix(Pawn ___pawn, out Ideo __state)
        {
            __state = ___pawn?.Ideo; // ideo before the change
        }

        static void Postfix(Pawn ___pawn, Ideo ideo, Ideo __state)
        {
            if (!ModsConfig.IdeologyActive) return;
            if (___pawn == null || ideo == null || Current.ProgramState != ProgramState.Playing) return;
            if (!___pawn.IsColonist) return;          // an identity event for our own pawns
            if (__state == null || __state == ideo) return; // no actual conversion

            SynapsePivotalMemory.Record(___pawn, SynapsePivotalMemory.Converted,
                $"{PivotalLifeEventHelpers.Name(___pawn)} converted to {ideo.name}.");
        }
    }

    // Prison-break attempt (base game). Targets the 1-arg overload explicitly to avoid an ambiguous match.
    [HarmonyPatch(typeof(PrisonBreakUtility), nameof(PrisonBreakUtility.StartPrisonBreak), new[] { typeof(Pawn) })]
    internal static class Patch_PrisonBreak_Start_Pivotal
    {
        static void Postfix(Pawn initiator)
        {
            if (initiator == null || Current.ProgramState != ProgramState.Playing) return;
            SynapsePivotalMemory.Record(initiator, SynapsePivotalMemory.EscapeAttempt,
                $"{PivotalLifeEventHelpers.Name(initiator)} attempted to escape imprisonment.");
        }
    }
}
