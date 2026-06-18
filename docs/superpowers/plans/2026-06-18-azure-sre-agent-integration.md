# Azure SRE Agent integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the bespoke SP5 SRE agent with leveraging the **Azure SRE Agent** product — remove the custom runtime/sweep/deploy, and have the provisioner render a per-instance onboarding runbook instead.

**Architecture:** Delete `src/dsf/sre/` + `dsfctl sre-sweep` + the `dsf-sre-<product>` Container App deploy. Add `render_sre_onboarding` (writes `sre-onboarding.md`) and swap the provisioner's `deploy_sre` step for a render-only `onboard_sre_agent`. Onboarding (wizard + OAuth at sre.azure.com) is interactive, so DSF only renders guidance + relies on prerequisites already created by earlier steps. Supersede ADR 0008 with ADR 0009.

**Tech Stack:** Python 3.12, uv, pytest (`asyncio_mode=auto`), ruff, pydantic. Spec: `docs/superpowers/specs/2026-06-18-azure-sre-agent-integration-design.md`. ADR: `docs/adr/0009-leverage-azure-sre-agent.md`.

**Conventions:** Each task ends green (`uv run pytest -q`, `uv run ruff check .`) and is committed with the `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>` trailer. `from __future__ import annotations` files need UNQUOTED self-referential return annotations (UP037). Branch: `refactor/azure-sre-agent` (off `main`).

---

### Task 1: Remove the bespoke SRE runtime + `sre-sweep` CLI

**Files:**
- Delete: `src/dsf/sre/` (whole package: `__init__.py`, `agent.py`, `detect.py`, `models.py`, `wiring.py`, `main.py`)
- Delete: `tests/sre/` (whole package: `__init__.py`, `test_agent.py`, `test_detect.py`, `test_wiring.py`)
- Modify: `src/dsf/cli/control.py` (remove `_cmd_sre_sweep` and the `sre-sweep` subparser)
- Test: `tests/cli/test_control.py` (drop `sre-sweep` from the subcommands tuple; delete the two `sre-sweep` tests; add a removed-assertion)

- [ ] **Step 1: Update the control-CLI tests to express the removal**

In `tests/cli/test_control.py`, change the subcommands tuple (remove `"sre-sweep"`):

```python
def test_cli_subcommands_importable():
    parser = build_parser()
    for cmd in ("run", "sweep", "serve-agent", "serve-orchestrator", "control-center"):
        args = parser.parse_args([cmd])
        assert args.command == cmd
```

Delete `test_cli_sre_sweep_parses_flags` and `test_cli_sre_sweep_dispatches` entirely, and add:

```python
def test_cli_sre_sweep_removed():
    import pytest

    parser = build_parser()
    with pytest.raises(SystemExit):
        parser.parse_args(["sre-sweep"])
```

- [ ] **Step 2: Delete the SRE test package and run to verify the new test fails**

```bash
rm -rf tests/sre
uv run pytest tests/cli/test_control.py::test_cli_sre_sweep_removed -q
```
Expected: FAIL — `sre-sweep` is still a registered subparser (no `SystemExit`).

- [ ] **Step 3: Remove the SRE runtime + sweep command**

Delete the package and the two control.py SRE pieces:

```bash
rm -rf src/dsf/sre
```

In `src/dsf/cli/control.py`, delete the whole `_cmd_sre_sweep` function (the `def _cmd_sre_sweep(...) -> int:` block, ~12 lines ending at `return 0`), and delete its subparser registration:

```python
    p_sre = sub.add_parser("sre-sweep", help="run one SRE sweep (fix-forward to the squad)")
    p_sre.add_argument("--dry-run", action="store_true", help="detect only, skip filing")
    p_sre.add_argument("--product", help="scope the sweep to a single product")
    p_sre.set_defaults(func=_cmd_sre_sweep)
```

- [ ] **Step 4: Run tests + lint to verify green**

```bash
uv run pytest tests/cli/test_control.py -q && uv run ruff check src/dsf/cli/control.py tests/cli/test_control.py
```
Expected: PASS, ruff clean. (`src/dsf/sre` and `tests/sre` are gone; `provisioner.py`/`runtime_render.py` still reference `render_sre_bundle`/`deploy_sre` — untouched here and still green.)

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(sre): remove bespoke SRE runtime + sre-sweep CLI (#30)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 2: Add `render_sre_onboarding` renderer (additive)

**Files:**
- Modify: `src/dsf/instance/runtime_render.py` (add `SreOnboarding` + `render_sre_onboarding`; import `HANDOFF_LABEL`; keep `render_sre_bundle` for now)
- Test: `tests/instance/test_runtime_render.py` (replace the `render_sre_bundle` test with a `render_sre_onboarding` test)

- [ ] **Step 1: Write the failing test**

In `tests/instance/test_runtime_render.py`, change the import line to add `render_sre_onboarding` (keep `render_sre_bundle` import for now since the function still exists):

```python
from dsf.instance.runtime_render import (
    render_runtime_bundle,
    render_sre_bundle,
    render_sre_onboarding,
    runtime_dir,
)
```

Replace `test_render_sre_bundle_writes_scoped_app_config` with:

```python
def test_render_sre_onboarding_writes_guided_runbook(tmp_path):
    onb = render_sre_onboarding(_manifest(tmp_path), repo_root=tmp_path)
    assert onb.runtime_dir == runtime_dir("microbi", tmp_path)
    assert onb.onboarding_path.name == "sre-onboarding.md"
    body = onb.onboarding_path.read_text(encoding="utf-8")
    assert "sre.azure.com" in body
    assert "rg-dsf-microbi" in body      # product resource group
    assert "swedencentral" in body       # region
    assert "acme/microbi" in body        # product repo
    assert "squad:ready" in body         # handoff label preserved
    assert "containerapp" not in body    # no Container App deploy
```

- [ ] **Step 2: Run test to verify it fails**

```bash
uv run pytest tests/instance/test_runtime_render.py::test_render_sre_onboarding_writes_guided_runbook -q
```
Expected: FAIL with `ImportError: cannot import name 'render_sre_onboarding'`.

- [ ] **Step 3: Implement the renderer**

In `src/dsf/instance/runtime_render.py`, add the import near the top (after the existing imports):

```python
from dsf.contracts.handoff import HANDOFF_LABEL
```

Add the dataclass after `SreBundle`:

```python
@dataclass(frozen=True)
class SreOnboarding:
    """Path to the rendered Azure SRE Agent onboarding runbook for one product."""

    runtime_dir: Path
    onboarding_path: Path
```

Add the renderer (after `render_sre_bundle`):

```python
def _render_sre_onboarding_md(
    *, product: str, resource_group: str, location: str, repo: str
) -> str:
    return (
        f"# Azure SRE Agent onboarding — {product}\n\n"
        "GENERATED by dsf (render_sre_onboarding). Do not edit.\n\n"
        "DSF leverages the **Azure SRE Agent** product instead of a bespoke SRE\n"
        "runtime (ADR 0009). Onboarding is interactive (wizard + OAuth); follow\n"
        "these per-instance steps to stand up the agent for this product.\n\n"
        "## 1. Create the agent\n\n"
        "1. Go to <https://sre.azure.com> and sign in.\n"
        "2. Start **Basics -> Review -> Deploy**.\n"
        f"3. Resource group: `{resource_group}` (region `{location}`); subscription:\n"
        "   the one that owns that resource group.\n"
        f"4. Agent name: `dsf-sre-{product}`. Model provider: Azure OpenAI for EU\n"
        "   data-residency tenants, otherwise the regional default.\n\n"
        "## 2. Connect the product repository\n\n"
        f"On the **Code** card, connect `{repo}` (GitHub, OAuth or PAT).\n\n"
        "## 3. Grant Azure resource access\n\n"
        f"On the **Azure Resources** card, grant the agent Reader on resource group\n"
        f"`{resource_group}`.\n\n"
        "## 4. Keep the squad handoff\n\n"
        "The agent files issues/PRs into the repo. Incident issues must carry the\n"
        f"`{HANDOFF_LABEL}` label so the **same** `squad triage --execute` intake\n"
        "picks them up — that label is already created by the `create_labels`\n"
        "provisioning step.\n"
    )


def render_sre_onboarding(
    manifest: InstanceManifest, *, repo_root: Path | None = None
) -> SreOnboarding:
    """Render ``sre-onboarding.md`` — the per-product Azure SRE Agent runbook.

    DSF leverages the Azure SRE Agent product (ADR 0009). Onboarding is
    wizard/OAuth-driven, so this renders deterministic guidance scoped to the
    product's resource group, region, and repository rather than deploying a
    custom runtime.
    """
    spec = manifest.spec
    product = spec.product
    rdir = runtime_dir(product, repo_root)
    rdir.mkdir(parents=True, exist_ok=True)
    onboarding_path = rdir / "sre-onboarding.md"
    onboarding_path.write_text(
        _render_sre_onboarding_md(
            product=product,
            resource_group=spec.resource_group(),
            location=spec.location,
            repo=spec.github_repo(),
        ),
        encoding="utf-8",
    )
    return SreOnboarding(runtime_dir=rdir, onboarding_path=onboarding_path)
```

Add both names to `__all__`:

```python
__all__ = [
    "RuntimeBundle",
    "SreBundle",
    "SreOnboarding",
    "render_runtime_bundle",
    "render_sre_bundle",
    "render_sre_onboarding",
    "runtime_dir",
]
```

- [ ] **Step 4: Run test + lint to verify green**

```bash
uv run pytest tests/instance/test_runtime_render.py -q && uv run ruff check src/dsf/instance/runtime_render.py tests/instance/test_runtime_render.py
```
Expected: PASS, ruff clean.

- [ ] **Step 5: Commit**

```bash
git add src/dsf/instance/runtime_render.py tests/instance/test_runtime_render.py
git commit -m "feat(instance): render per-product Azure SRE Agent onboarding runbook (#30)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 3: Swap provisioner `deploy_sre` → render-only `onboard_sre_agent`

**Files:**
- Modify: `src/dsf/instance/provisioner.py` (import `render_sre_onboarding`; rename the step; replace the execute branch with render-only)
- Modify: `src/dsf/cli/factory.py:103` (help text)
- Test: `tests/instance/test_provisioner.py` (step-list rename; two execute tests)
- Test: `tests/cli/test_factory.py:39` (`deploy_sre` -> `onboard_sre_agent`)

- [ ] **Step 1: Update the tests to the new step name + behavior**

In `tests/instance/test_provisioner.py`, in the step-order assertion change `"deploy_sre"` to `"onboard_sre_agent"`:

```python
    assert [s.name for s in plan.steps] == [
        "create_repo",
        "create_labels",
        "squad_init",
        "squad_copilot",
        "squad_triage",
        "create_resource_group",
        "provision_azure",
        "deploy_council",
        "onboard_sre_agent",
        "write_config",
    ]
```

In `test_apply_execute_runs_real_steps_and_deploys_sre` (rename to `test_apply_execute_runs_real_steps_and_onboards_sre`), delete the `sre_update = next(...)` block and its `assert "--image" in sre_update`, then replace the SRE result assertion + add a no-container-app guard:

```python
    # no SRE Container App is deployed — onboarding is wizard/OAuth (ADR 0009):
    assert not any(
        cmd[:3] == ["az", "containerapp", "update"]
        and cmd[cmd.index("--name") + 1] == "dsf-sre-demo"
        for cmd in executed
    )
    assert manifest.executed is True
    results = {s.name: s.result for s in manifest.plan.steps}
    assert results["create_repo"] == "executed"
    assert results["deploy_council"] == "deployed"
    assert results["onboard_sre_agent"] == "onboarding ready"
```

In `test_apply_execute_aca_updates_container_app`, change the SRE bundle assertion and result:

```python
    assert (runtime / "sre-onboarding.md").is_file()
```
and
```python
    assert results["onboard_sre_agent"] == "onboarding ready"
```
(replacing the `sre.containerapp.yaml` file assertion and `results["deploy_sre"] == "deployed"`).

- [ ] **Step 2: Update the factory CLI test**

In `tests/cli/test_factory.py`, change line ~39:

```python
    assert "onboard_sre_agent" in out
```

- [ ] **Step 3: Run the tests to verify they fail**

```bash
uv run pytest tests/instance/test_provisioner.py tests/cli/test_factory.py -q
```
Expected: FAIL — plan still emits `deploy_sre`, and execute still deploys `dsf-sre-demo`.

- [ ] **Step 4: Update the provisioner + factory help text**

In `src/dsf/instance/provisioner.py`, change the import on line 21:

```python
from dsf.instance.runtime_render import render_runtime_bundle, render_sre_onboarding
```

Rename the plan step (was `name="deploy_sre"`):

```python
            ProvisionStep(
                name="onboard_sre_agent",
                description=f"Render the Azure SRE Agent onboarding runbook for {s.product}",
            ),
```

Replace the `elif step.name == "deploy_sre":` branch in `apply` with a render-only branch:

```python
                elif step.name == "onboard_sre_agent":
                    provisional = InstanceManifest(
                        spec=self.spec, plan=plan, executed=executed, azure=azure_result
                    )
                    render_sre_onboarding(provisional, repo_root=self._repo_root)
                    step.result = (
                        "onboarding ready" if execute else "rendered (dry-run)"
                    )
```

In `src/dsf/cli/factory.py` line ~103, update the help text:

```python
        help="run executable steps (gh/squad/az + council bring-up + SRE onboarding)",
```

- [ ] **Step 5: Run tests + lint to verify green**

```bash
uv run pytest tests/instance/test_provisioner.py tests/cli/test_factory.py -q && uv run ruff check src/dsf/instance/provisioner.py src/dsf/cli/factory.py
```
Expected: PASS, ruff clean.

- [ ] **Step 6: Commit**

```bash
git add src/dsf/instance/provisioner.py src/dsf/cli/factory.py tests/instance/test_provisioner.py tests/cli/test_factory.py
git commit -m "refactor(instance): onboard_sre_agent renders Azure SRE Agent runbook (no Container App) (#30)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 4: Remove the now-dead `render_sre_bundle` / `SreBundle`

**Files:**
- Modify: `src/dsf/instance/runtime_render.py` (delete `SreBundle`, `render_sre_bundle`, drop from `__all__`)
- Test: `tests/instance/test_runtime_render.py` (drop the now-unused `render_sre_bundle` import)

- [ ] **Step 1: Remove the dead code**

In `src/dsf/instance/runtime_render.py`, delete the `SreBundle` dataclass (the `@dataclass(frozen=True)` block with `class SreBundle`) and the entire `render_sre_bundle` function. Remove `"SreBundle"` and `"render_sre_bundle"` from `__all__`. The shared `_render_containerapp` helper stays (still used by `render_runtime_bundle`); update its docstring reference from "SRE (``deploy_sre``)" to just the council runtime.

In `tests/instance/test_runtime_render.py`, drop `render_sre_bundle` from the import block:

```python
from dsf.instance.runtime_render import (
    render_runtime_bundle,
    render_sre_onboarding,
    runtime_dir,
)
```

- [ ] **Step 2: Verify nothing else references the removed symbols**

```bash
grep -rn "render_sre_bundle\|SreBundle\|sre.containerapp\|dsf-sre-" src tests
```
Expected: no matches.

- [ ] **Step 3: Run tests + lint to verify green**

```bash
uv run pytest tests/instance -q && uv run ruff check src/dsf/instance/runtime_render.py
```
Expected: PASS, ruff clean.

- [ ] **Step 4: Commit**

```bash
git add src/dsf/instance/runtime_render.py tests/instance/test_runtime_render.py
git commit -m "refactor(instance): drop dead render_sre_bundle/SreBundle (#30)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 5: Docs — supersede ADR 0008, rewrite RUNBOOK + charter + registry note

**Files:**
- Modify: `docs/adr/0008-sre-agent.md` (mark Superseded by ADR 0009)
- Modify: `docs/RUNBOOK.md` (rewrite the SRE section, lines ~132–160)
- Modify: `docs/superpowers/specs/2026-06-17-dark-software-factory-template-charter-design.md` (SP5 row + SRE-tech decision)
- Modify: `src/dsf/agents/registry.py` (drop the stale "scheduled sweep / Container App" wording)

- [ ] **Step 1: Mark ADR 0008 superseded**

In `docs/adr/0008-sre-agent.md`, change the status line:

```markdown
- Status: Superseded by [ADR 0009](0009-leverage-azure-sre-agent.md)
```

And add, immediately under the header, a one-line banner:

```markdown
> **Superseded by [ADR 0009](0009-leverage-azure-sre-agent.md):** DSF now leverages
> the Azure SRE Agent product instead of this bespoke agent. Kept for history.
```

- [ ] **Step 2: Rewrite the RUNBOOK SRE section**

Replace the `## SRE agent (fix-forward)` section (through the end of its content, ~lines 132–162) with:

```markdown
## SRE (Azure SRE Agent)

DSF leverages the **Azure SRE Agent** product (ADR 0009) — not a bespoke runtime.
Provisioning an instance renders a per-product onboarding runbook at
`config/instances/<product>.runtime/sre-onboarding.md` (the `onboard_sre_agent`
step). Onboarding itself is interactive (wizard + OAuth):

1. Open <https://sre.azure.com>, create an agent in the product resource group
   (`rg-dsf-<product>`).
2. Connect the product repository (GitHub OAuth or PAT).
3. Grant the agent Reader on `rg-dsf-<product>`.

Once connected, the Azure SRE Agent investigates incidents and files issues/PRs
into the product repo. Keep the `squad:ready` label (created by `create_labels`)
on incident issues so the **same** `squad triage --execute` intake picks them up:

```
Azure SRE Agent -> investigates -> files issue/PR (squad:ready)
  -> squad triage --execute -> Copilot coding agent -> PR -> human review
```
```

Also update the line near the top of the RUNBOOK that mentions "the product's SRE
agent runtime (both Azure Container Apps)" (~line 67) to drop the SRE Container App
(only the orchestrator runtime is an Azure Container App now).

- [ ] **Step 3: Update the charter SP5 row + SRE-tech decision**

In `docs/superpowers/specs/2026-06-17-dark-software-factory-template-charter-design.md`:

- SP5 row (~line 128): change to note the product-leverage approach, e.g.
  `| SP5 ✅ | SRE agent *(done — ADR 0009: leverage the Azure SRE Agent product)* | Provision prerequisites + render a per-instance onboarding runbook; the Azure SRE Agent files issues/PRs into the product repo via the SP4 handoff label. |`
- SRE-tech decision (~line 141): replace the "Start dsf-native (reuse Sentry/Grafana backends + reflection store)" text with: `**SRE agent tech.** Leverage the **Azure SRE Agent** product (ADR 0009); DSF provisions prerequisites and renders an onboarding runbook. Resolved.`
- The capability/architecture mentions (~lines 45, 96) that describe the SRE agent as observing via Sentry/Grafana backends: add a parenthetical `(now via the Azure SRE Agent product — ADR 0009)` so the roadmap narrative stays consistent. Do not rewrite the historical SP1–SP4 narrative.

- [ ] **Step 4: Fix the registry note**

In `src/dsf/agents/registry.py`, change the module-docstring sentence about the SRE agent to:

```python
The SRE agent is intentionally absent: SRE is handled by the Azure SRE Agent
product (ADR 0009), not an A2A-served app.
```

- [ ] **Step 5: Verify + commit**

```bash
grep -rn "sre-sweep\|dsfctl sre-sweep\|dsf.sre\|render_sre_bundle" src docs/RUNBOOK.md docs/adr/0008-sre-agent.md
uv run ruff check src/dsf/agents/registry.py
```
Expected: no live references in RUNBOOK/registry/0008 (historical mentions in superseded ADR text or dated `docs/superpowers/plans/*` are fine). Then:

```bash
git add docs/adr/0008-sre-agent.md docs/RUNBOOK.md docs/superpowers/specs/2026-06-17-dark-software-factory-template-charter-design.md src/dsf/agents/registry.py
git commit -m "docs(sre): supersede ADR 0008, retarget RUNBOOK/charter to Azure SRE Agent (#30)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 6: Full verification + PR

- [ ] **Step 1: Full suite, lint, eval gate, offline dry-run**

```bash
uv run ruff check .
uv run pytest -q
uv run python -m dsf.evals.runner --gate
uv run dsf provision --dry-run --product demo --owner acme || true   # if the factory CLI exposes provision; else skip
uv run dsfctl run --dry-run --signal tests/fixtures/sample_signal.json
```
Expected: ruff clean; all tests pass (SRE tests gone; new renderer/provisioner tests pass); eval gate PASSED; dry-run runs the council line.

- [ ] **Step 2: Confirm the bespoke SRE is fully gone**

```bash
grep -rn "dsf\.sre\|sre-sweep\|render_sre_bundle\|deploy_sre\|sre\.containerapp\|dsf-sre-" src tests
```
Expected: no matches in `src/` or `tests/`.

- [ ] **Step 3: Push + open PR**

```bash
git push -u origin refactor/azure-sre-agent
gh pr create --base main --head refactor/azure-sre-agent \
  --title "refactor: leverage the Azure SRE Agent product (remove bespoke SRE) (#30)" \
  --body "<summary referencing the spec + ADR 0009; closes #30>"
```
Expected: PR created, CI green + MERGEABLE.

---

## Self-Review notes

- **Spec coverage:** removal (Task 1, 4) ✓; `render_sre_onboarding` (Task 2) ✓; `onboard_sre_agent` render-only step (Task 3) ✓; handoff preserved (renderer body + Task 5 RUNBOOK) ✓; offline tests (Tasks 2–3) ✓; ADR 0009 supersede + RUNBOOK + charter (Task 5) ✓.
- **Always-green ordering:** renderer is added before `render_sre_bundle` is removed (Task 2 additive → Task 3 switch → Task 4 delete), so no task leaves a dangling import.
- **Type consistency:** `SreOnboarding(runtime_dir, onboarding_path)` and `render_sre_onboarding(manifest, *, repo_root)` are used identically in the renderer, the test, and the provisioner branch.
- **No fabricated Azure calls:** `onboard_sre_agent` renders only; the suite asserts no `az containerapp update --name dsf-sre-*` (Task 3).
