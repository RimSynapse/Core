using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace RimSynapse
{
    /// <summary>
    /// Registered game tools for the memory debug commands (Core#81). Thin wrappers over
    /// <see cref="SynapseCoreDebug"/> so the Direct Action Console and the harness can drive them.
    /// </summary>
    public static partial class SynapseToolRegistry
    {
        private static void RegisterDebugMemoryTools()
        {
            RegisterTool(
                "debug_add_memory",
                "DEBUG: seed a weighted memory on a colonist for testing the memory engine. Weight is on a 0-1 scale (values above 1 are clamped). Optionally mark it about another colonist to link it for consolidation.",
                new
                {
                    type = "object",
                    properties = new
                    {
                        pawnName = new { type = "string", description = "Colonist who holds the memory" },
                        summary = new { type = "string", description = "The memory text" },
                        memoryType = new { type = "string", description = "e.g. social, EventReflection, Therapy (default EventReflection)" },
                        weight = new { type = "number", description = "0.0-1.0 importance (default 0.5)" },
                        tags = new { type = "array", items = new { type = "string" }, description = "Optional tags" },
                        subjectPawnName = new { type = "string", description = "Optional: the colonist this memory is ABOUT (links it in the salience graph)" },
                        isLongTerm = new { type = "boolean", description = "Optional: force long-term (never decays)" }
                    },
                    required = new[] { "pawnName", "summary" }
                },
                DebugAddMemoryHandler, false, null, true);

            RegisterTool(
                "debug_dump_memories",
                "DEBUG: list a colonist's memories with weight, salience, long-term tier, reference count and tags.",
                new
                {
                    type = "object",
                    properties = new { pawnName = new { type = "string", description = "Colonist to inspect" } },
                    required = new[] { "pawnName" }
                },
                DebugDumpMemoriesHandler, true);

            RegisterTool(
                "debug_run_memory_maintenance",
                "DEBUG: force the daily memory decay + salience/consolidation pass on a colonist now, instead of waiting an in-game day. Reports how many memories pruned/consolidated.",
                new
                {
                    type = "object",
                    properties = new { pawnName = new { type = "string", description = "Colonist to run maintenance on" } },
                    required = new[] { "pawnName" }
                },
                DebugRunMaintenanceHandler, false, null, true);
        }

        private static Dictionary<string, object> ParseArgs(string args)
        {
            try { return JsonConvert.DeserializeObject<Dictionary<string, object>>(args) ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }

        private static string DebugAddMemoryHandler(string args)
        {
            try
            {
                var d = ParseArgs(args);
                var pawn = SynapseCoreDebug.FindPawn(d.TryGetValue("pawnName", out var pn) ? pn?.ToString() : null);
                if (pawn == null) return "{\"error\": \"pawn not found\"}";
                string summary = d.TryGetValue("summary", out var s) ? s?.ToString() : null;
                if (string.IsNullOrEmpty(summary)) return "{\"error\": \"summary is required\"}";
                string memoryType = d.TryGetValue("memoryType", out var mt) ? mt?.ToString() : "EventReflection";
                float weight = d.TryGetValue("weight", out var w) && w != null ? Convert.ToSingle(w) : 0.5f;
                bool isLongTerm = d.TryGetValue("isLongTerm", out var lt) && lt is bool lb && lb;
                var tags = new List<string>();
                if (d.TryGetValue("tags", out var tg) && tg is Newtonsoft.Json.Linq.JArray arr)
                    tags = arr.Select(x => x.ToString()).ToList();
                var subject = d.TryGetValue("subjectPawnName", out var sp) && sp != null
                    ? SynapseCoreDebug.FindPawn(sp.ToString()) : null;

                string memId = SynapseCoreDebug.AddMemory(pawn, summary, memoryType, weight, tags, subject, isLongTerm);
                return JsonConvert.SerializeObject(new { ok = true, pawn = pawn.LabelShort, memId });
            }
            catch (Exception ex) { return JsonConvert.SerializeObject(new { error = ex.Message }); }
        }

        private static string DebugDumpMemoriesHandler(string args)
        {
            var pawn = SynapseCoreDebug.FindPawn(ParseArgs(args).TryGetValue("pawnName", out var pn) ? pn?.ToString() : null);
            if (pawn == null) return "{\"error\": \"pawn not found\"}";
            return JsonConvert.SerializeObject(new { pawn = pawn.LabelShort, dump = SynapseCoreDebug.DumpMemories(pawn) });
        }

        private static string DebugRunMaintenanceHandler(string args)
        {
            var pawn = SynapseCoreDebug.FindPawn(ParseArgs(args).TryGetValue("pawnName", out var pn) ? pn?.ToString() : null);
            if (pawn == null) return "{\"error\": \"pawn not found\"}";
            return JsonConvert.SerializeObject(new { ok = true, result = SynapseCoreDebug.RunMaintenance(pawn) });
        }
    }
}
