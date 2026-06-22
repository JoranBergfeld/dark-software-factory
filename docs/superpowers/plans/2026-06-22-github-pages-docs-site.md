# Plan — Public Docs Site on GitHub Pages

Executes the design `docs/superpowers/specs/2026-06-22-github-pages-docs-site-design.md`.
MkDocs + Material site under `docs/site/`, deployed to GitHub Pages via Actions. ADRs and
`docs/superpowers/` stay repo-only and off the site.

**Verification gate (every phase ends here):**
`uv run --group docs mkdocs build --strict` (zero warnings) + existing gates untouched
(`uv run ruff check .`, `uv run lint-imports`, `uv run pytest -q`).

**Link rule:** site→site links are relative (e.g. `the-loop.md`); site→repo-only files
(ADRs, `docs/superpowers/`, code, `.env.example`) become absolute GitHub blob URLs
(`https://github.com/JoranBergfeld/dark-software-factory/blob/main/<path>`) so `--strict`
never sees a dangling in-tree link.

## Phase A — Tooling skeleton
1. `pyproject.toml`: add `docs = ["mkdocs-material>=9.5"]` to `[dependency-groups]`.
2. `mkdocs.yml` (root): Material theme; `docs_dir: docs/site`; site_url/repo_url; palette
   toggle; markdown_extensions incl. `pymdownx.superfences` custom **mermaid** fence;
   `plugins: [search]`; `nav` mirroring spec §3. Build is run with `--strict`.
3. `.gitignore`: ignore mkdocs build output `/site/`.

## Phase B — Site content (`docs/site/`)
All nav targets must exist for `--strict`.
- `index.md` (Home) — from README intro + loop Mermaid + "people outside the loop".
- `concept/the-loop.md` — README "The loop" (overview + Mermaid + 3 phase blurbs); links to
  the three phase pages.
- `concept/feature-council.md` — relocate `docs/phases/feature-council.md`; fix links
  (`../../README.md#the-loop`→`the-loop.md`; ADR/superpowers→GitHub URLs).
- `concept/coding-squad.md` — relocate `docs/phases/coding-squad.md`; same link fixes.
- `concept/sre-agent.md` — relocate `docs/phases/sre-agent.md`; same link fixes.
- `concept/the-harness.md` — from README "The harness".
- `get-started/quickstart.md` — from `docs/GETTING_STARTED.md` (prereqs/install/verify);
  fix links (`../README.md`→site/GitHub; `RUNBOOK.md`→`operate.md`; `.env.example`/`adr/`→GitHub).
- `get-started/provision-a-factory.md` — `dsf new` from GETTING_STARTED + RUNBOOK "Creating
  a product instance".
- `get-started/operate.md` — relocate `docs/RUNBOOK.md` operate content; fix links.

## Phase C — Remove old + repoint cross-refs
- Delete `docs/phases/`, `docs/GETTING_STARTED.md`, `docs/RUNBOOK.md`.
- Repoint repo-internal references to the new in-repo paths (`docs/site/...`):
  `CLAUDE.md` (3), `.github/copilot-instructions.md` (3), `infra/README.md` (1),
  `infra/main.bicep` comment (1).
- Slim `README.md` to developer entry point (spec §8): one-paragraph intro + loop Mermaid +
  "Read the docs →" site link + workspace layout + canonical commands + run-docs-locally +
  pointer to `docs/adr/`.

## Phase D — Deploy workflow
- `.github/workflows/docs.yml`: PR build-guard + push-to-main build+deploy +
  `workflow_dispatch`; path filters; `build` (uv sync --group docs → mkdocs build --strict →
  upload-pages-artifact) and `deploy` (main only, `github-pages` env, deploy-pages);
  permissions `contents: read, pages: write, id-token: write`; concurrency `pages`. Actions
  pinned to SHAs: checkout `34e1148…`, setup-uv `caf0cab…`, configure-pages `983d773…`,
  upload-pages-artifact `56afc60…`, deploy-pages `d6db901…`.

## Phase E — Final verification
- `mkdocs build --strict` zero warnings; built `site/` has no `adr/`/`superpowers/` paths.
- Repo grep: no stale in-tree links to `docs/phases/`, `docs/RUNBOOK.md`,
  `docs/GETTING_STARTED.md` (only GitHub-URL or site references remain).
- `ruff` / `lint-imports` / `pytest` green (docs group adds no runtime imports).
- One-time manual (documented, not automatable): repo Settings → Pages → Source = GitHub Actions.
