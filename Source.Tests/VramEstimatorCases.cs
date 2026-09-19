using System;
using System.Collections.Generic;
using RimSynapse;
using RimAgentic.Testing;

namespace RimSynapse.Tests
{
    /// <summary>
    /// The model VRAM estimator (Core #126): now the single source, in <c>VramAdvisor</c>
    /// (partial across VramAdvisor.cs / VramAdvisor_Advisory.cs), with the NVIDIA-Tool duplicate
    /// retired (#124). This pins the model-name → billions → GB parse table so the dedupe cannot
    /// silently drift: standard "Nb" patterns, MoE/expert "eNb"/"aNb" notation, the
    /// mini/small/medium/large keyword fallback, and the quantization-marker skip.
    /// </summary>
    [SynapseTestSet]
    public static class VramEstimatorCases
    {
        // Q4_K_M: ~0.65 GB per billion params + ~0.5 GB KV/runtime overhead.
        private static float Est(float billions) => billions * 0.65f + 0.5f;

        public static IEnumerable<SynapseTestCase> All()
        {
            yield return new SynapseTestCase("Core_VramEstimatorParseTable", () =>
            {
                void Check(string name, float expectGb, string why)
                {
                    float got = VramAdvisor.EstimateModelVramGb(name);
                    Assert.True(Math.Abs(got - expectGb) < 0.01f, $"{why}: '{name}' → {got:0.###}, expected {expectGb:0.###}");
                }

                // Standard "Nb" parameter patterns.
                Check("llama-3-8b", Est(8f), "plain 8b");
                Check("qwen2.5-7b-instruct", Est(7f), "7b with trailing tag, not the 2.5");
                Check("gemma-2-27b", Est(27f), "two-digit 27b");
                Check("phi-3-mini-3.8b", Est(3.8f), "decimal 3.8b (explicit number beats the 'mini' keyword)");
                Check("qwen-0.5b", Est(0.5f), "sub-1B 0.5b");

                // MoE / expert notation — active parameter count.
                Check("gemma-3n-e4b", Est(4f), "expert notation e4b (standard regex must not eat it)");

                // Keyword fallback when there is no explicit size.
                Check("phi-3-mini", Est(3.8f), "mini keyword");
                Check("some-small-model", Est(7f), "small keyword");
                Check("a-medium-llm", Est(13f), "medium keyword");
                Check("the-large-one", Est(34f), "large keyword");

                // No parseable size, and quantization markers must not be read as a size.
                Check("", 0f, "empty name");
                Check("mystery-model", 0f, "no size, no keyword");
                Check("model-q8b", 0f, "q8b is a quantization marker, not 8B");

                return "parse table pinned";
            });
        }
    }
}
