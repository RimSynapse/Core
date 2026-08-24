using System.Collections.Generic;
using System.Linq;
using Verse;

namespace RimSynapse.Internal
{
    public static class ModelDefUtility
    {
        public static List<string> GetModelsForProvider(ApiProvider provider, LlmCapabilities requiredCaps = LlmCapabilities.None)
        {
            var list = new List<string>();

            // If it's LM Studio, we still fetch from the dynamically loaded ModelManager
            if (provider == ApiProvider.Local_LMStudio)
            {
                if (ModelManager.CachedModelIds != null)
                {
                    list.AddRange(ModelManager.CachedModelIds);
                }
                return list;
            }

            // Custom providers might not have defs, so we check if any custom provider matches
            if (provider == ApiProvider.Custom)
            {
                // Custom models are just whatever the user typed into the custom provider definition
                var settings = RimSynapseMod.Instance.Settings;
                if (settings != null && settings.customProviders != null)
                {
                    foreach (var c in settings.customProviders)
                    {
                        if (requiredCaps == LlmCapabilities.None || (c.caps & requiredCaps) == requiredCaps)
                        {
                            if (!string.IsNullOrEmpty(c.model))
                            {
                                list.Add(c.model);
                            }
                        }
                    }
                }
                // Custom providers don't have predefined Defs in XML usually, unless users add them.
                // We'll still fall through to check XML defs just in case someone made a Def for ApiProvider.Custom.
            }

            // For OpenAI, Gemini, Claude, Pollinations, read from DefDatabase
            foreach (var def in DefDatabase<ModelDef>.AllDefs.Where(d => d.provider == provider))
            {
                if (requiredCaps == LlmCapabilities.None || (def.Capabilities & requiredCaps) == requiredCaps)
                {
                    list.Add(def.modelId);
                }
            }

            return list.Distinct().ToList();
        }

        public static List<string> GetAllModels(LlmCapabilities requiredCaps = LlmCapabilities.None)
        {
            var list = new List<string>();
            foreach (var provider in (ApiProvider[])System.Enum.GetValues(typeof(ApiProvider)))
            {
                list.AddRange(GetModelsForProvider(provider, requiredCaps));
            }
            return list.Distinct().ToList();
        }

        public static void ShowModelSelector(ApiProvider provider, LlmCapabilities requiredCaps, System.Action<string> onSelect)
        {
            if (provider == ApiProvider.Local_LMStudio)
            {
                // Fetch models off the main thread; ModelManager runs the query on a background
                // thread and invokes this callback back on the main thread (updating its cache
                // along the way), so opening the selector never freezes the game while LM Studio
                // responds or spins a model back up (Core#120).
                ModelManager.GetModels(result =>
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    if (result.online && result.modelIds != null)
                    {
                        foreach (var am in result.modelIds)
                        {
                            string localM = am;
                            options.Add(new FloatMenuOption(localM, () => onSelect(localM)));
                        }
                    }
                    if (options.Count == 0)
                    {
                        options.Add(new FloatMenuOption("No models found", null));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                });
            }
            else
            {
                var availableModels = GetModelsForProvider(provider, requiredCaps);
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (var am in availableModels)
                {
                    string localM = am;
                    options.Add(new FloatMenuOption(localM, () => onSelect(localM)));
                }
                if (options.Count == 0)
                {
                    options.Add(new FloatMenuOption("No models found", null));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
        }
    }
}
