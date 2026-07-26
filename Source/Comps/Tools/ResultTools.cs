using System.Collections.Generic;
using Newtonsoft.Json;

namespace RimSynapse
{
    /// <summary>
    /// Retrieval side of the result-handle scheme: when a tool result was too large to
    /// inline, the model received an excerpt plus a "res_N" key and can pull the full
    /// payload here on demand.
    /// </summary>
    public static partial class SynapseToolRegistry
    {
        private static void RegisterResultTools()
        {
            RegisterTool(
                "get_stored_result",
                "Retrieve the full content of a previously truncated tool result by its key (e.g. 'res_3'). Use when an earlier result was cut short and you need the rest.",
                new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["key"] = new Dictionary<string, object>
                        {
                            ["type"] = "string",
                            ["description"] = "The stored result key, exactly as given in the truncation notice (e.g. 'res_3')."
                        }
                    },
                    ["required"] = new List<string> { "key" }
                },
                args =>
                {
                    string key = null;
                    try
                    {
                        var parsed = JsonConvert.DeserializeObject<Dictionary<string, object>>(args ?? "{}");
                        if (parsed != null && parsed.TryGetValue("key", out var k)) key = k?.ToString();
                    }
                    catch
                    {
                        return "{\"error\": \"Invalid arguments; expected {\\\"key\\\": \\\"res_N\\\"}.\"}";
                    }

                    if (string.IsNullOrEmpty(key))
                        return "{\"error\": \"Missing 'key' argument.\"}";

                    if (!SynapseResultStore.TryGet(key, out var content))
                        return $"{{\"error\": \"No stored result under '{key}' — it may have been evicted (the store keeps the most recent {SynapseResultStore.MaxEntries}).\"}}";

                    return content;
                });
        }
    }
}
