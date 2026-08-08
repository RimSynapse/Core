using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using RimSynapse.Utils;

namespace RimSynapse
{
    /// <summary>
    /// LLM-as-judge mechanism. Given an OUTPUT and the CRITERIA it should meet, asks the model for a
    /// structured verdict (pass / score / reasoning).
    ///
    /// <para>Deliberately NOT wired into the gameplay test suite's pass/fail — those use deterministic
    /// structured-field assertions. This is a reusable mechanism for (a) Core debug use cases, and
    /// (b) LLM-Trainer evaluation/training pipelines, where a fuzzy "was this a reasonable decision?"
    /// judgement is wanted.</para>
    /// </summary>
    public static class SynapseLlmJudge
    {
        public class Verdict
        {
            public bool valid;        // a parseable verdict came back
            public bool pass;
            public float score;       // 0..1 confidence the output meets the criteria
            public string reasoning;
        }

        /// <summary>
        /// Deterministic parse of a judge response. Separated from the LLM call so it can be unit-tested
        /// without a live model (the gameplay suite covers this; the live judgement is exercised at playtest).
        /// </summary>
        public static Verdict Parse(string content)
        {
            var v = new Verdict();
            try
            {
                string json = JsonHelper.ExtractJson(content);
                if (string.IsNullOrEmpty(json)) return v;
                var d = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);
                if (d == null) return v;
                if (d.TryGetValue("pass", out var p) && p != null) v.pass = Convert.ToBoolean(p);
                if (d.TryGetValue("score", out var s) && s != null) v.score = Convert.ToSingle(s);
                if (d.TryGetValue("reasoning", out var r)) v.reasoning = r?.ToString();
                v.valid = true;
            }
            catch { v.valid = false; }
            return v;
        }

        /// <summary>Judge OUTPUT against CRITERIA via the live LLM; the verdict is delivered to the callback.</summary>
        public static void Judge(string output, string criteria, Action<Verdict> onVerdict, string label = "LLM Judge")
        {
            string systemPrompt = @"You are an impartial evaluator. Given an OUTPUT and the CRITERIA it should meet, decide whether the output meets the criteria.
Respond ONLY as valid JSON, no markdown:
{ ""pass"": true, ""score"": 0.0, ""reasoning"": ""one or two sentences"" }
- pass: your overall yes/no judgement.
- score: 0.0-1.0 confidence that the output meets the criteria.
- reasoning: a brief justification.";
            string userMessage = $"CRITERIA:\n{criteria}\n\nOUTPUT:\n{output}";
            var options = new ChatOptions { priority = 4, requestName = label, targetName = "judge" };

            SynapseClient.PromptAsync(RimSynapseMod.ModHandle, systemPrompt, userMessage, result =>
            {
                if (!result.success) { onVerdict?.Invoke(new Verdict { valid = false, reasoning = result.error }); return; }
                onVerdict?.Invoke(Parse(result.content));
            }, options);
        }
    }
}
