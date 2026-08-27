// Exposes Core's internal members to the in-repo test assembly so cases can exercise
// internal surfaces (e.g. SynapseToolRegistry.DispatchBreakResolution, Core#120) without
// widening the public API companion mods bind to.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("RimSynapseCoreTests")]
