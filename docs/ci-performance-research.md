# PR #226: CI duration investigation

Date: 2026-10-19. Source examined: `fix/pr-226-review`, commit `7431fb7`.
The initial research below was read-only. The follow-up measurement section records
subsequently authorized local experiments and narrow test-fixture improvements.
No changes were pushed and no GitHub Actions runs were started.

## Initial research conclusions

- **Hours are being spent in tests, not the initial build.** The canceled run built in 76 seconds; its test step ran for 1h42m23s without completing.
- **This was not a healthy, merely slow run:** two individual failures were already logged before cancellation.
- **Strongest source-supported explanation:** repeated full lifecycle preparation and nested real HTTP-host execution inside test doubles, multiplied across many cases. Production polling also adds unavoidable wall time despite injected clocks.
- **Confirmed isolation defect:** one runtime test reaches the real Azure credential chain before its scripted decision runner. This explains its observed 15-second failure, **not** the entire multi-hour suite.
- **Do not claim a precise dominant CPU/wait function yet.** No profiler, per-phase timing, host-start counts, allocation trace, or complete per-test duration artifact was collected. The source proves repeated work and several real delays; it does not attribute all three hours.
- Prefer reducing repeated work and repairing isolation over sharding: sharding can hide elapsed time without reducing aggregate GitHub minutes.

## Evidence and operational status

Actions observations below were supplied by the coordinating investigation, not independently fetched by this source-code investigation.

| Evidence | Observation |
| --- | --- |
| [Earlier run 38727334201](https://github.com/JoranBergfeld/dark-software-factory/actions/runs/38727334201/job/116396335534) | CLI: 1,839 tests, 3h13m. Runtime: 1,263 tests, 21m14s. Core: 388ms; FeatureCouncil: 3s; ControlCenter: 4s; module boundaries: 145ms. |
| [Canceled run 39261756412](https://github.com/JoranBergfeld/dark-software-factory/actions/runs/39261756412/job/118090916647) | Build 13:37:54–13:39:10Z, 76s; parity 7s; Test 13:39:17–15:21:40Z, 1h42m23s. Runtime: 1,271 tests, 20m54s, one failure. CLI had not completed. |
| Runtime failure | `RuntimeVerbsTests.Uncertain_onboarding_sweep_resumes_the_same_durable_invocation_on_the_next_tick` failed after 15s: expected `RuntimeVerbException`, received `Azure.Identity.CredentialUnavailableException`. Managed Identity, Azure CLI, and PowerShell credential attempts appeared in logs. |
| CLI failure | `FactoryReconfigurationTransitionTests.Resume_conditionally_applies_configuration_and_invalidates_old_evidence_and_activation` failed after 9m16s at 14:42:03Z. Assertion at line 385 saw `Unknown`: “Live rollout authority, native request or receipt unconfirmed. Retain original operation; no replay, adoption, reset or activation.” |
| Cancellation | Run concluded canceled at 15:21:44Z; coordinator verified zero active and zero queued Actions runs and stopped the local watcher. Earlier run had already failed. |
| Earlier local measurements, supplied | Twenty selected lifecycle tests took 1h42m; activated-fixture initialization approximately 10m; individual tests approximately 2–6m. Not remeasured here. |

**Status correction:** an earlier “no failures reported yet” statement reflected job-level status only. It missed already-failed individual tests; neither absence of a completed job nor continued test execution implies passing tests.

The CLI failure's root cause remains unresolved. Its duration is evidence of expensive execution, not proof that timeout caused `Unknown`: that result also covers unconfirmed native authority/request/receipt. The failing section performs configuration, evaluation, activation approval, and a fresh live rollout before the assertion ([source](../dotnet/tests/Dsf.Cli.Tests/FactoryReconfigurationTransitionTests.cs#L355-L386)).

## Ranked mechanisms

Ranking reflects likely suite-wide impact, not measured percentage attribution.

### 1. Repeated lifecycle preparation and nested host execution

**Confirmed structure; likely broadest cost multiplier.**

The activated fixture does substantially more than populate an in-memory dictionary:

1. `ActivatedFactoryFixture.InitializeAsync` calls `PublisherFixture.InitializeAsync`, then reviews/applies publication and final promotion ([FactoryActiveWorkflowTests.cs:889–918](../dotnet/tests/Dsf.Cli.Tests/FactoryActiveWorkflowTests.cs#L889-L918)).
2. Publisher initialization calls `FactoryActivationTests.ReadyAsync`, approves activation, and reviews/applies live rollout ([FactoryPublisherTests.cs:174–201](../dotnet/tests/Dsf.Cli.Tests/FactoryPublisherTests.cs#L174-L201)).
3. `ReadyAsync` configures the preview and executes a real council evaluation through the CLI surface ([FactoryActivationTests.cs:116–138](../dotnet/tests/Dsf.Cli.Tests/FactoryActivationTests.cs#L116-L138)).
4. Preview configuration itself performs three CLI invocations after preparation ([PreviewEvaluationTests.cs:85–99](../dotnet/tests/Dsf.Cli.Tests/PreviewEvaluationTests.cs#L85-L99)).
5. Preparation creates a charter scenario, adopts its charter, and prepares telemetry ([PreviewPreparationTransport.cs:38–55](../dotnet/tests/Dsf.Cli.Tests/PreviewPreparationTransport.cs#L38-L55)).
6. Charter setup prepares source and models, verifies models, acquires source, and runs inference repeatedly until ready ([FactoryCharterWorkflowTests.cs:1178–1208](../dotnet/tests/Dsf.Cli.Tests/FactoryCharterWorkflowTests.cs#L1178-L1208)).

These are **real production orchestration/state transitions over fake external backends**, not real Azure provisioning or LLM inference. `ReservationMachine.InvokeAsync` invokes the CLI in-process; it does not run `dotnet` for each lifecycle command ([FactoryReservationTests.cs:1064–1075](../dotnet/tests/Dsf.Cli.Tests/FactoryReservationTests.cs#L1064-L1075)).

However, fake HTTP hops frequently start real loopback Kestrel hosts:

- Every source-host request creates a runner and invokes `serve-source` ([FactorySourceWorkflowTests.cs:186–207](../dotnet/tests/Dsf.Cli.Tests/FactorySourceWorkflowTests.cs#L186-L207)).
- A nested model-host request creates another runner and invokes `serve-models` ([FactoryIntentCommandTests.cs:108–121](../dotnet/tests/Dsf.Cli.Tests/FactoryIntentCommandTests.cs#L108-L121)).
- `SourceHostRunner` and `IntentModelRunner` start a web application, make one loopback HTTP request, and stop it ([SourceInventoryTransport.cs:448–477](../dotnet/tests/Dsf.Runtime.Tests/SourceInventoryTransport.cs#L448-L477), [IntentModelTransport.cs:34–58](../dotnet/tests/Dsf.Runtime.Tests/IntentModelTransport.cs#L34-L58)).
- Runtime tests repeat the pattern independently: initial model host in `IntentWorkflowTransport.CreateAsync`, new source host per `RunAsync`, new model host per forwarded model request ([FactoryIntentWorkflowTests.cs:995–1015,1066–1098](../dotnet/tests/Dsf.Runtime.Tests/FactoryIntentWorkflowTests.cs#L995-L1098)).
- Production host construction uses `WebApplication.CreateBuilder`; `RuntimeCliApplication` owns/disposes these hosts ([FactorySourceHost.cs:15–21](../dotnet/src/Dsf.Runtime/FactorySourceHost.cs#L15-L21), [RuntimeCliApplication.cs:108–126](../dotnet/src/Dsf.Runtime/RuntimeCliApplication.cs#L108-L126)).

Cheap static counts in CLI test `*.cs` files:

| Explicit helper reference | Call sites | Files |
| --- | ---: | ---: |
| `CharterScenario.ReadyAsync(` | 122 | 9 |
| `PreviewEvaluationTests.ConfiguredAsync(` | 58 | 13 |
| `PreviewPreparationTransport.CreateAsync(` | 18 | 5 |
| `PreviewPublicationTests.PreparedAsync(` | 12 | 4 |
| `FactoryActivationTests.ReadyAsync(` | 6 | 4 |

These are textual call-site counts, **not executions or independent additive totals**. Helpers call one another; theories multiply some calls.

**Important qualifications:**

- Activated fixtures already use **class fixtures**, not full initialization per test. Different test classes still get different fixtures.
- `PublisherFixture.Reset` restores snapshots but also calls `Restart`; `Restart` stops preview hosts and reconstructs the source transport ([FactoryPublisherTests.cs:201–233](../dotnet/tests/Dsf.Cli.Tests/FactoryPublisherTests.cs#L201-L233), [PreviewEvaluationTransport.cs:20–32](../dotnet/tests/Dsf.Cli.Tests/PreviewEvaluationTransport.cs#L20-L32)).
- Preview/live/model-proof hosts already have a per-hostname lazy cache. Do not propose adding a cache that already exists ([PreviewPublicationTransport.cs:80–149](../dotnet/tests/Dsf.Cli.Tests/PreviewPublicationTransport.cs#L80-L149)).
- Host startup/shutdown could be cheap relative to validation/serialization. Source inspection alone cannot choose between those costs.

**Minimal proposal:** measure setup/host/request phases first; then reuse immutable serialized prepared-state snapshots and isolated per-test clones for tests whose subject is downstream behavior. Keep a small full-lifecycle contract suite generating/verifying those snapshots through real production paths. Reuse source/model hosts within a scenario where restart is not the behavior under test. Preserve explicit restart/reconstruction tests and separate clients for concurrency tests.

### 2. Repeated transport reconstruction and serialization

**Confirmed repeated work; CPU/allocation attribution unmeasured.**

The source fake rebuilds an entire `SourceInventoryTransport` on fallback management GETs, even before simple permission/list branches return ([FactorySourceWorkflowTests.cs:215–236](../dotnet/tests/Dsf.Cli.Tests/FactorySourceWorkflowTests.cs#L215-L236)). Its constructor deserializes authority, constructs native resources/effects, hashes receipts, rewrites configuration, and builds source data ([SourceInventoryTransport.cs:70–199](../dotnet/tests/Dsf.Runtime.Tests/SourceInventoryTransport.cs#L70-L199)).

`SourceWorkflowTransport.Values()` round-trips the complete configuration listing through JSON on each call ([FactorySourceWorkflowTests.cs:180–183](../dotnet/tests/Dsf.Cli.Tests/FactorySourceWorkflowTests.cs#L180-L183)); the underlying store already offers a revision-invalidated value snapshot ([FactoryReservationTests.cs:1122–1128](../dotnet/tests/Dsf.Cli.Tests/FactoryReservationTests.cs#L1122-L1128)).

**Minimal proposal:** avoid full fake reconstruction for simple responses; obtain current native resources from a revision-aware fixture projection. Use the existing value snapshot for internal fixture lookup, retaining serialized HTTP boundaries and conditional-write semantics. Never cache away authority changes, ETags, hostile mutations, or drift detection that tests intentionally exercise.

### 3. Real wall-time waits coexist with fake clocks

**Confirmed waits; cumulative contribution unmeasured.**

| Site | What actually happens |
| --- | --- |
| [OnboardPreviewEvaluation.cs:115–154](../dotnet/src/Dsf.Cli/OnboardPreviewEvaluation.cs#L115-L154) | While native preview is queued/running/checking, production code sleeps **5 real seconds** per iteration. Request deadlines use injected `clock`, this delay does not. |
| [FactoryLiveExercise.cs:25–55](../dotnet/src/Dsf.Cli/FactoryLiveExercise.cs#L25-L55) | Equivalent **5-real-second** live polling loop; same clock mismatch. |
| [FactoryModelsTimeoutTests.cs:115–149](../dotnet/tests/Dsf.Cli.Tests/FactoryModelsTimeoutTests.cs#L115-L149) | One test deliberately performs five **24-second real sleeps**, and asserts at least **120s** elapsed. This cost is certain when the test reaches that path. |
| [FactoryActiveWorkflowTests.cs:952–977](../dotnet/tests/Dsf.Cli.Tests/FactoryActiveWorkflowTests.cs#L952-L977) | Helper advances fake clock by 100ms only after waiting up to 100ms wall time; five-minute outer limit. Decision helper polls up to 2,000 times with 100ms sleeps, **plus request duration**. |
| [FactoryPromotionTests.cs:232–239](../dotnet/tests/Dsf.Cli.Tests/FactoryPromotionTests.cs#L232-L239) | Another up-to-1,000-iteration 100ms poll loop, plus request time. |

These are additive overhead and possible long-tail waits. A five-second poll alone does not explain ten-minute setup unless there are many polls or expensive work underneath it.

**Do not confuse simulated time with sleeping:**

- `EvaluationModelDelay = 20 seconds` advances `SourceTestClock.Now`, then sleeps only **10ms** ([PreviewPublicationTransport.cs:164–167](../dotnet/tests/Dsf.Cli.Tests/PreviewPublicationTransport.cs#L164-L167)).
- `SourceTestClock` implements controllable one-shot timers ([SourceInventoryTransport.cs:359–424](../dotnet/tests/Dsf.Runtime.Tests/SourceInventoryTransport.cs#L359-L424)).
- `WaitAsync(TimeSpan.FromMinutes(3))` is a maximum wait, not a compulsory three-minute delay.
- Existing infrastructure 10-second delays generally already accept a clock; replacing every timeout constant would be both ineffective and unsafe.

**Minimal proposal:** make remaining production polling delays use the existing time-provider seam, preserving production intervals. Drive test time from explicit request/worker milestones, not wall-time lockstep or blind unlimited auto-advance. Replace the deliberate 120s wait with controlled time and a short transport-wiring test that still detects accidentally applying a 100s client timeout to a 120s aggregate. Retain cancellation, expiry, retry, uncertain-dispatch, and ingress-budget assertions; do not simply delete them.

### 4. Recent collection serialization increases elapsed time

**Confirmed scheduling change; not the original cause.**

[`FactoryLifecycleIntegrationCollection.cs:5–9`](../dotnet/tests/Dsf.Cli.Tests/FactoryLifecycleIntegrationCollection.cs#L5-L9) disables parallelization for the collection used by ActiveWorkflow, EvidenceRefresh, PublicationReconciliation, and Audit classes. Their files contain 33 facts and 73 inline-data declarations across 25 theories; this is a static inventory, not a discovered-test count.

Classes in this collection cannot overlap one another and the nonparallel collection does not overlap parallel collections. It does **not** create one shared `ActivatedFactoryFixture`, nor does it make `Task.WhenAll`/barrier-controlled concurrency inside a test sequential.

The earlier 3h13m CLI result predates this serialization, so blaming it for the original hours would be incorrect.

**Proposal:** retain isolation until shared-state assumptions are verified. Remove unnecessary collection-wide exclusion only after fixtures are independently owned and bounded. Preserve the actual concurrent-writer/race tests; restoring test-class concurrency is not a substitute for them.

### 5. A real credential/network path escaped test doubles

**Confirmed by source and CI exception stack. Fix before any rerun.**

Activation path:

1. The failing test supplies a fake configuration gateway and `ScriptedOnboardingDecisionRunner`, but no `PreviewCredential` or `PreviewHttp` ([RuntimeVerbsTests.cs:372–395](../dotnet/tests/Dsf.Runtime.Tests/RuntimeVerbsTests.cs#L372-L395)).
2. `TestDependencies.Build` does not populate those properties ([TestDependencies.cs:17–47](../dotnet/tests/Dsf.Runtime.Tests/TestDependencies.cs#L17-L47)).
3. Scheduled sweep resolves its durable invocation before calling the decision runner. `ResolveScheduledInvocationAsync` reads the journal outside its write exception handler ([RuntimeWorkflow.cs:205–233](../dotnet/src/Dsf.Runtime/RuntimeWorkflow.cs#L205-L233)).
4. `ScheduledInvocationStore` defaults to **`DefaultAzureCredential` and a real HTTP client**, rather than anything supplied by the scripted decision runner ([RuntimeWorkflow.cs:272–286](../dotnet/src/Dsf.Runtime/RuntimeWorkflow.cs#L272-L286)). That client has infinite HTTP timeout ([RuntimeWorkflow.cs:17–18](../dotnet/src/Dsf.Runtime/RuntimeWorkflow.cs#L17-L18)).
5. `ReadCheckpointAsync` requests a token before sending Cosmos HTTP ([CosmosCheckpointTransport.cs:24–28,56–64](../dotnet/src/Dsf.Runtime/CosmosCheckpointTransport.cs#L24-L64)).

The observed exception occurs during credentials, so these logs **do not prove an authenticated Cosmos request was sent**. The source proves a real outbound path is reachable if credentials succeed. The intended scripted error is never reached; the failure does not establish that the scripted decision's resume behavior is wrong.

**Minimal proposal:** inject a synthetic credential **and** a stateful conditional Cosmos HTTP fake in this test; verify the same durable invocation and dry-run bit survive the scripted failure. A token stub alone is insufficient because real HTTP would remain. Make test dependency defaults reject unexpected external I/O immediately. Keep production adapters real, per [ADR 0014](adr/0014-real-only-src-no-offline.md); no production offline fallback.

Fifteen seconds cannot explain 20m54s of Runtime or hours of CLI. Do not generalize this proven leak to all lifecycle tests: their host compositions explicitly inject credentials/HTTP transports ([PreviewPublicationTransport.cs:124–139](../dotnet/tests/Dsf.Cli.Tests/PreviewPublicationTransport.cs#L124-L139)).

### 6. Child processes/builds: secondary, not lifecycle's main mechanism

- CLI executable tests invoke `dotnet run --no-build`, with a 60s process deadline ([CliSurfaceTests.cs:560–608](../dotnet/tests/Dsf.Cli.Tests/CliSurfaceTests.cs#L560-L608)); cutover tests likewise pass `--no-build` ([CutoverAcceptanceTests.cs:321–352](../dotnet/tests/Dsf.Cli.Tests/CutoverAcceptanceTests.cs#L321-L352)).
- One release metadata test invokes `dotnet run --project ...` **without** `--no-build`/`--no-restore`, plus `openssl`; its helper has no process deadline ([ReleaseArtifactAutomationTests.cs:208–268](../dotnet/tests/Dsf.Cli.Tests/ReleaseArtifactAutomationTests.cs#L208-L268)).
- No lifecycle `dotnet build`/`pack` loop was found. Build/pack text in release-contract assertions is not evidence those commands execute.

**Proposal:** prebuild the metadata generator once, run its built artifact, and bound child-process execution/output collection. Lower priority than lifecycle costs; no evidence this accounts for hours.

## Safe immediate containment versus root-cause changes

### Containment — proposals only

1. Keep the already-canceled expensive run stopped; do not relaunch the full suite to “see if it finishes.”
2. Add a deliberate job timeout and per-PR stale-run cancellation policy. These cap waste, not fix test speed.
3. Add per-test TRX reporting, incremental phase logs, and best-effort artifact upload on failure/cancellation; abrupt termination may prevent TRX finalization. Do not wait for job completion to inspect individual failures.
4. Preserve required coverage. A bounded diagnostic subset is not a replacement green signal for an omitted lifecycle suite.
5. Do not increase runners or shard as the first response; the user is minimizing total runner minutes.

The coordinator inspected [`.github/workflows/ci.yml`](../.github/workflows/ci.yml): it declares no explicit job timeout or concurrency group. One job runs the entire solution test suite after the parity gate, then repeats the module-boundary project and performs packaging/dependency checks. It declares no TRX logger or test-result artifact upload. The repeated small gates are not the multi-hour bottleneck. No workflow was changed.

### Root-cause work order

1. Seal the proven credential/HTTP leak with deterministic stateful journal coverage.
2. Measure one representative setup/operation, with host starts/stops, request counts, serialized bytes, CPU/allocation and wall-time spans.
3. Remove redundant fake rebuilding/serialization and per-request host creation where those spans show significant cost.
4. Move remaining polling and long timeout tests onto controlled time; retain a small real-transport contract layer.
5. Split full lifecycle preparation coverage from downstream state-matrix coverage using verified immutable snapshots and isolated stores.
6. Revisit collection exclusion only after correctness/isolation evidence; separately diagnose the reconfiguration `Unknown` failure.

## Proposed validation and cost budgets

These are **future stop limits**, not claims about current runtime or authorization to execute tests now.

| Stage | Evidence required | Maximum cost before stopping |
| --- | --- | --- |
| Read-only evidence | Existing logs/TRX, static counts; no new Actions run | Already completed; no test compute |
| Isolation repair | One scheduled-resume test, synthetic credential + conditional store, outbound attempts fail closed | 15s test ceiling, 60s process ceiling |
| Primitive profile | A bounded number of fake native reads/host hops, setup phases individually timed; no activated fixture | 60s overall; stop and inspect partial trace |
| Controlled-time tests | Poll delay advancement, 120s logical aggregate, cancellation, ingress limit, no wall-time 120s wait | 60s combined |
| Representative lifecycle | Only after primitive improvements; one full setup plus representative activation/reconfiguration/restart path | 120s setup target; 5m hard process budget, no automatic retry |
| Concurrency validation | Barrier-controlled writer races, independent stores, original invocation/ETag/receipt/no-replay assertions | 5m targeted batch, once |
| Full validation | Only after targeted gates pass; compare same-runner test-duration distribution and aggregate minutes, not just elapsed time | Proposed 15m suite acceptance target; if exceeded, stop and revisit rather than shard |

Use external process deadlines in addition to test deadlines: fixture initialization, infinite HTTP timeout, synchronous teardown, and polling request duration can escape a simplistic “number of sleeps” budget. Do not switch all hosts to mocks or omit serialized restart/concurrency coverage to meet a timing target.

## Remaining uncertainty

- No profiler evidence distinguishes expensive production validation/serialization, test-fake reconstruction, host startup/shutdown, lock contention, thread-pool starvation, GC, and scheduler overhead.
- No evidence currently establishes an Azure SDK retry storm across the lifecycle suite, file-watcher overhead, leaked hosts, or repeated RSA generation as the dominant cause. Those remain hypotheses, not findings.
- The 9m16s reconfiguration failure needs a bounded, phase-instrumented reproduction after setup cost is reduced; extending its timeout or weakening `Unknown` checks is not justified.
- Static call-site counts are lower-level structural evidence, not a complete cost model.
- Repository guidance and ADR 0014 were read. The ADR contains historical Python-era examples; the current .NET instruction remains authoritative: real adapters in production, deterministic doubles in test projects.
- During the initial research, only this note was added; no tests or builds were run. The authorized follow-up below supersedes that initial execution status.

## Follow-up: bounded local measurements and first improvement

### Method and limits

Used the existing Release test harness and real lifecycle orchestration over its
existing fake external transports. Individual diagnostic invocations had a
65-second process deadline with a five-second termination grace. No full suite,
hosted action, or external Azure operation was initiated.

Temporary instrumentation recorded monotonic elapsed time, process CPU time,
process-wide cumulative managed allocations, and invocation counts. Timings of
nested spans overlap: source-host totals include model calls and must not be
added together. CPU time can exceed elapsed time because multiple threads run.
Allocated bytes measure allocation churn, **not live heap size or peak memory**.

The missing `dotnet-trace` tool was installed into the session directory, not the
repository or global tool store. Eight-second EventPipe samples supplemented
the phase counters. An initial trace was truncated when the test host exited;
it was discarded for attribution and replaced with complete shorter traces.
All temporary test instrumentation and probe tests were removed after measurement.

Artifacts are retained in the session `files/` directory:

- `ci-perf-phases-before.json`: isolated charter preparation.
- `ci-perf-activated-before.json`: last completed activation sample before cutoff.
- `ci-perf-activated.nettrace`: complete late-phase sampled-thread trace.
- `ci-perf-preview-before.json` and `ci-perf-preview-after.json`: matched complete workloads.
- `ci-perf-final-validation.log`: final correctness and allocation regression results.

### What the measurements established

One `CharterScenario.ReadyAsync()` completed in **6.81s**, consuming **0.970GB**
of managed allocations. A full `ActivatedFactoryFixture.InitializeAsync()` did
not complete within the 65-second process budget. Its last sample at 62s showed
**82.2 CPU seconds and 28.7GB cumulative allocations**.

That activation sample included:

| Measured operation | Count | Cumulative elapsed |
| --- | ---: | ---: |
| Source inventory construction | 1,643 | 2.06s |
| Source fixture value extraction | 1,691 | 5.32s |
| Source/model host start and stop | 48 source starts, 47 stops; 160 model starts/stops | 0.464s |

This rules out host start/stop as the first optimization target for this sample.
Nested request execution is expensive; merely keeping more servers alive would
not eliminate the validation, serialization, and fake reconstruction inside it.
The late-phase trace repeatedly traversed `FactoryIntentAccess.CheckAsync`,
`FactorySourceAccess.CheckAsync`, `AzureConfigurationSettingsGateway.ListRecordsAsync`,
and JSON serialization/deserialization. Sampled thread time includes idle
threads, so raw percentages must not be presented as wall-time attribution.

### Approved narrow changes

Only [`SourceWorkflowTransport`](../dotnet/tests/Dsf.Cli.Tests/FactorySourceWorkflowTests.cs)
was optimized:

1. `Values()` now uses the existing revision-invalidated
   `ReservationTestStore.Values()` snapshot, returning an independent dictionary.
   It no longer serializes the entire configuration listing and reparses every
   unchanged payload on every call.
2. Management GETs construct a source inventory only for resource types that
   actually consume that inventory. Permission/assignment lists and other
   fallback requests no longer build and discard unrelated source resources.

No production reads were cached or removed. HTTP serialization, conditional
writes, ETags, native identity checks, host boundaries, and actual lifecycle
transitions remain in place. Store mutation/replacement still invalidates or
replaces the value snapshot.

A separate serialized-listing experiment measured only 1.09MB allocated for
256KiB of payload. The proposed raw-string concatenation was not established as
an improvement, so that path was left unchanged.

### Matched before/after comparison

Both runs executed the same complete
`PreviewEvaluationTests.ConfiguredAsync(publisher: true)` workload, including
157 model-host and 46 source-host requests. They ran sequentially with the same
phase instrumentation, without the sampling profiler.

| Metric | Original reads | Optimized reads | Interpretation |
| --- | ---: | ---: | --- |
| Elapsed | 48.49s | 47.56s | About 2% lower; one pair is insufficient to claim a reliable wall-time gain |
| CPU | 67.13s | 64.97s | About 3% lower |
| Cumulative allocated bytes | 11.948GB | 10.247GB | **1.701GB / 14.2% less allocation** |
| Inventory constructions | 661 | 378 | 283 unnecessary constructions removed |
| Inventory construction time | 1.656s | 0.734s | Reduced repeated fixture work |
| Value extraction calls | 707 | 424 | Fewer callers need values |
| Value extraction time | 2.563s | 0.148s | Existing snapshot avoids payload round-trips |

The small deterministic regression is stronger evidence of the specific fix:
ten unchanged reads of 32 eight-KiB records previously allocated **19,345,920
bytes** and failed the 256KiB allocation budget. They now allocate **11,680
bytes** with the temporary instrumentation removed. The threshold allows
dictionary copies without relying on runner speed.

### Correctness and remaining work

[`SourceWorkflowTransportTests`](../dotnet/tests/Dsf.Cli.Tests/SourceWorkflowTransportTests.cs)
adds six cases covering allocation growth, independent snapshots, mutation and
store replacement, conditional writes/deletes, stale-write rejection, and
permission/assignment listings without unrelated source construction.

Final Release build: zero warnings/errors. The six new cases, complete
`FactorySourceWorkflowTests` class, and two real charter-context regressions
passed **17/17 in 31.48s**. They include lost-response recovery, replaced native
resources, rechecked governance, serialized worker reconstruction, malformed
adoption, and retained original evidence.

This is a verified allocation improvement, **not a solution to multi-hour CI**.
The matched workload still allocates 10.25GB. The next investigation should
count and time repeated governance listings and validations per logical
operation, then distinguish avoidable fixture work from intentionally fresh
production authority checks. Do not remove fences or share mutable authority
across requests merely to reduce timings.

The scheduled-sweep credential leak and reconfiguration failure remain separate
unresolved correctness issues. Full activation and full CI were not rerun to
completion. No commits, pushes, or CI restarts were made during this follow-up.

## Authorized bounded CI retry, 2026-10-20

The user subsequently authorized publishing these changes and retrying CI with
a ten-minute limit. The CI build job now declares `timeout-minutes: 10`, enforced
by GitHub independently of this session. This budget includes setup, build, and
tests; it is not an extra ten minutes for each step.

The solution test command uses normal console verbosity to report individual
completed tests and their durations before the overall suite finishes. This
does not guarantee a start event for every unfinished test. If the job reaches
its limit, inspect the recorded progress rather than automatically retrying or
increasing the limit. Other workflows triggered by the push have independent
jobs; this setting applies to the `ci` workflow's build job.
