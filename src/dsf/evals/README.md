# Evals — golden set + evaluators + CI gate

Phase 8 (plan Task 8.1). Runs the **dry-run conveyor** over a small golden set
and scores each outcome, then gates CI on aggregate metrics.

```
python -m dsf.evals.runner --gate      # exits non-zero on any sub-threshold metric
```

## Layout

- `golden/cases.json` — the golden cases (one signal per case + expectations).
- `evaluators.py` — `groundedness`, `routing_accuracy`, `verdict_match`.
- `runner.py` — `run_case`, `run_suite`, `gate`, and the `main(--gate)` CLI.

## How the line actually behaves (and why cases are shaped this way)

The in-process **fixture-backed** source agents read their *own* fixtures
(`tests/fixtures/<source>_evidence.json`) regardless of the signal payload. So a
case cannot steer *what* evidence appears via the payload — only *which sources*
participate (via `source_kinds`) and *which product* the resulting issues route
to (because the fixtures carry their own `product_hints`).

Cases are therefore authored so the enabled sources' fixtures route to the
case's `expected_product`:

| enabled source | fixture `product_hints` (first) | routes to       |
| -------------- | ------------------------------- | --------------- |
| `SENTRY`       | `microbi`                       | `microbi`       |
| `WEBIQ`        | `microbi`                       | `microbi`       |
| `GRAFANA`      | `microbi`                       | `microbi`       |

With the default local backends the dry-run line gathers fixture evidence, synthesizes a
grounded proposal per product cluster, passes the council, routes to a product,
and reaches **FILED** with grounded `RoutedIssue`s (no real GitHub call — dry
run). Multi-source cases set `expected_product: null` (routing unconstrained).

## Evaluators

- **`groundedness(run, issues, proposals=None)`** — fraction of routed issues
  whose originating proposal's `evidence_ids` are *all* present in
  `run.evidence` (and non-empty). `1.0` = everything filed was grounded. The
  dry-run grounding gate (S4) strips ungrounded ids, so a healthy line scores
  `1.0`. No issues ⇒ `1.0` (nothing ungrounded was filed).
- **`routing_accuracy(issues, expected_product)`** — fraction of issues routed
  to `expected_product`. `expected_product is None` ⇒ `1.0` (unconstrained). A
  product expected but no issues produced ⇒ `0.0`.
- **`verdict_match(run, expect_filed)`** — `1.0` if `(status == FILED) ==
  expect_filed`, else `0.0`.

## The KILLED case (`all-agents-disabled-killed`)

Exercises `verdict_match` on a **not-filed** expectation. Two mechanisms:

1. `config_overrides` disables **every** source agent (`agent.SENTRY` …
   `agent.TICKETS` → `false`), so no evidence is gathered and no proposal is
   synthesized — the "no evidence → no proposal → not filed" path.
2. `setup.seed_debounce: true` makes the runner pre-seed an in-flight signal
   record matching the case's signal `text`, so S1's debounce fires and the run
   terminates **KILLED** (the only deterministic status that is not `FILED`).
   With `expect_filed: false`, `verdict_match` scores `1.0`.

The seed is *test scaffolding* applied by the runner (`_apply_setup`), not part
of production ingestion. It models a duplicate in-flight signal arriving while
an identical one is already being processed.

## Gate thresholds

Aggregate (mean) metric must meet:

| metric           | threshold |
| ---------------- | --------- |
| groundedness     | ≥ 0.99    |
| routing_accuracy | ≥ 0.80    |
| verdict_match    | ≥ 0.80    |

`gate(result)` returns the count of sub-threshold metrics (0 = pass). It reads
`result["metrics"]` only, so a test can call it with a fabricated low-metric
dict to assert the regression path returns non-zero.
