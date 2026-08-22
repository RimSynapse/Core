#!/usr/bin/env bash
# Runs Core's behaviour suites without RimWorld, Unity, Harmony or a game install.
#
# Core is mostly not testable this way — it is an HTTP client, a Harmony patch set and a pile of
# WorldComponent state, all of which need the game. What is testable is the parts deliberately
# built without game dependencies, and the public surfaces other mods compile against. Those are
# worth pinning precisely because a break in them is silent: a companion mod registers into a
# renamed member, gets no error, and simply never answers.
#
#   Ubuntu/WSL:  sudo apt-get install -y mono-mcs mono-runtime
#   Then:        Tests/run-tests.sh
#
# Exits non-zero if any suite fails to build or fails an assertion. Each binary is removed before
# its build so a compile failure can never be masked by a stale binary reporting a stale pass.
set -u

cd "$(dirname "$0")/.." || exit 1

SRC=Source
OUT="${TMPDIR:-/tmp}/core-tests"
mkdir -p "$OUT"

MCS_FLAGS="-target:exe -langversion:latest -nowarn:0169,0414,0649,0219"
failures=0

run_suite() {
    name=$1; shift
    binary="$OUT/$name.exe"
    rm -f "$binary"

    if ! mcs $MCS_FLAGS -out:"$binary" "$@"; then
        echo "BUILD FAILED: $name"
        failures=$((failures + 1))
        return
    fi

    if ! mono "$binary"; then
        failures=$((failures + 1))
    fi
}

run_suite providers \
    Tests/Stubs.cs Tests/ProviderTests.cs \
    $SRC/SynapseCoreProviders.cs

# The deferred-event pipeline is game-free, so it needs no stubs at all.
run_suite deferred \
    Tests/DeferredEventTests.cs \
    $SRC/SynapseDeferredEventPipeline.cs

# The tool vocabulary (Core #63) is game-free by design: the allowlist and the
# difficulty clamp compile with no stubs. Executor enforcement is Tier-2.
run_suite vocabulary \
    Tests/ToolVocabularyTests.cs \
    $SRC/Comps/SynapseToolVocabulary.cs

# The difficulty mood mandate (Core #66) is the pure slider→stance mapping.
# Live gating and slider reads are Tier-2.
run_suite mood \
    Tests/DifficultyMoodTests.cs \
    $SRC/Comps/SynapseDifficultyMood.cs

# The storyteller decision gate (Core #67) is the pure "beat due?" + "may begin?" logic.
# The live cadence source, async selection and vanilla fallback are Tier-2.
run_suite decisiongate \
    Tests/StorytellerDecisionGateTests.cs \
    $SRC/Comps/StorytellerDecisionGate.cs

# The GpuStats in-process consumers channel (Core #104) is game-free: upsert-by-modId and
# the resident→vram rule. Live registration/read is cross-mod (Local TTS / NVIDIA Tool).
run_suite gpuconsumer \
    Tests/GpuConsumerTests.cs \
    $SRC/Models/GpuStats.cs

echo
if [ "$failures" -eq 0 ]; then
    echo "ALL SUITES PASSED"
    exit 0
fi

echo "$failures SUITE(S) FAILED"
exit 1
