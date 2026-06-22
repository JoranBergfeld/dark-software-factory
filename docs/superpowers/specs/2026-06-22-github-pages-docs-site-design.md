# Dark Software Factory — Public Docs Site on GitHub Pages (Design)

**Status:** Proposed (2026-06-22) — pending review
**Scope:** Stand up a public-facing documentation site for this repository on **GitHub
Pages**, built with **MkDocs + Material**. The site becomes the front door for the
**concept** (what DSF is and why) and **how to use it** (getting started, provisioning,
operating). Conceptual and how-to prose currently living in `README.md`, `docs/phases/`,
`docs/GETTING_STARTED.md`, and `docs/RUNBOOK.md` moves onto the site, so the repository
itself can be dedicated to
*building the thing*. Implementation specifics — ADRs and `docs/superpowers/` — stay
repo-only and are **not** surfaced on the site. This is an implementation-ready design for
a single sub-project; a separate plan follows.

---

## 1. Goal & boundary

Today the repo explains the concept (README, phases) and how to operate it (RUNBOOK) as
raw Markdown that you read on GitHub. The goal is to lift that public-facing narrative onto
a polished website and leave the repo focused on code.

**The boundary that drives every decision below:**

| Surface | Holds | Examples |
|---|---|---|
| **Public site** (`docs/site/`) | Concept + how-to-use | the loop, the three phases, the harness, quickstart, `dsf new`, operating the line |
| **Repo only** (unchanged location) | Build / implementation specifics | code, `docs/adr/`, `docs/superpowers/`, `CLAUDE.md`, copilot instructions |

ADRs are explicitly **out** of the public site (they are implementation specifics). The
site source still lives in this repo (under `docs/site/`) and is versioned with the code;
"moved off the repo" means moved out of the repo's front-matter prose (README) and into a
rendered site, not into a separate repository.

## 2. Core decisions (locked in this brainstorm)

| Decision | Choice |
|---|---|
| Audience & purpose | Public concept site **plus** getting-started / how-to-use docs |
| Site source location | **Same repo**, dedicated `docs/site/` content folder (versioned with code) |
| Content depth | **Narrative only** — concept + guides; **no** auto-generated code/API reference |
| Tooling | **MkDocs + Material** theme (Python-native, Markdown, native Mermaid) |
| ADRs & `superpowers/` | **Repo-only**, not rendered on the site |
| RUNBOOK | Operational how-to-use → **relocated** into the site's *Get started → Operate* |
| Phase write-ups | **Relocated** from `docs/phases/` into the site's *Concept* section |
| README after migration | **Slim developer entry point**: what-it-is + link to site + build info only |
| Hosting | GitHub Pages, **deployed via GitHub Actions** (Pages source = "GitHub Actions") |
| URL | Default project pages URL `https://joranbergfeld.github.io/dark-software-factory/` |
| Out of scope | Custom domain, versioned docs (mike), API reference, blog |

## 3. Information architecture (site map)

```
Home (index.md)            the concept in brief: dark-factory metaphor, the loop,
                           "people outside the loop"
Concept/
  The loop                 overview + Mermaid of the 3-phase loop
  Feature Council          ← docs/phases/feature-council.md
  Coding Squad             ← docs/phases/coding-squad.md
  SRE Agent                ← docs/phases/sre-agent.md
  The harness              governance dials (from README "The harness")
Get started/
  Quickstart               fresh clone → make install → verify (← docs/GETTING_STARTED.md)
  Provision a factory      dsf new <product> (← GETTING_STARTED.md + RUNBOOK)
  Operate it               dsfctl, Control Center, sweeps, guardrails (← docs/RUNBOOK.md)
```

`Home` and `Concept/The loop` and `Concept/The harness` are authored from the README's
conceptual prose. The three phase pages, the getting-started guide, and the operating guide
are **relocated existing files**, lightly edited for a site context (e.g. fixing relative
links, trimming any deep code-path tails that read as implementation detail rather than
concept).

## 4. Repository layout

`mkdocs.yml` sits at the repo root (MkDocs convention; what `mkdocs` and Pages tooling
expect). All rendered content lives under `docs/site/`:

```
mkdocs.yml                         # site config (root)
docs/
  site/                            # docs_dir — PUBLIC site content ONLY
    index.md                       # Home
    concept/
      the-loop.md
      feature-council.md           # ← relocated from docs/phases/
      coding-squad.md              # ← relocated from docs/phases/
      sre-agent.md                 # ← relocated from docs/phases/
      the-harness.md
    get-started/
      quickstart.md                # ← relocated from docs/GETTING_STARTED.md
      provision-a-factory.md
      operate.md                   # ← relocated from docs/RUNBOOK.md
    assets/                        # images, if any
  adr/                             # unchanged, repo-only (not in site)
  superpowers/                     # unchanged, repo-only (not in site)
```

After migration, `docs/phases/`, `docs/GETTING_STARTED.md`, and `docs/RUNBOOK.md` no longer
exist at their old paths.

### Cross-reference updates (no dangling links)

Relocating files means updating every reference to the old paths. Known references to fix:

- `CLAUDE.md` — "Phase write-ups are in `docs/phases/`"; "the operational runbook is
  `docs/RUNBOOK.md`".
- `.github/copilot-instructions.md` — same two references.
- `README.md` — the two links to `docs/GETTING_STARTED.md` (lines ~90, ~101) and the
  `docs/phases/` link repoint to the site's *Get started* / *Concept* sections.
- The relocated `GETTING_STARTED.md`'s own internal links (to `../README.md`, `RUNBOOK.md`,
  `adr/`) are rewritten for their new site location.
- A repo-wide grep for `docs/phases`, `RUNBOOK`, and `GETTING_STARTED` gates completion —
  zero stale references remain.

## 5. MkDocs configuration

`mkdocs.yml` (Material theme), key elements:

- `site_name`, `site_description`, `site_url:
  https://joranbergfeld.github.io/dark-software-factory/`, `repo_url`,
  `repo_name: JoranBergfeld/dark-software-factory`.
- `docs_dir: docs/site`.
- `theme: material` with features `navigation.sections`, `navigation.top`,
  `navigation.instant`, `content.code.copy`, `search.suggest`; light/dark `palette` toggle.
- `markdown_extensions`: `admonition`, `attr_list`, `md_in_html`, `tables`,
  `toc` (permalink), `pymdownx.highlight`, `pymdownx.superfences` with a **custom Mermaid
  fence** so the README's existing ` ```mermaid ` blocks render natively.
- `plugins: [search]` (built-in; Material renders Mermaid client-side via superfences — no
  extra plugin needed).
- `nav:` mirroring section 3.
- Build runs with `--strict` so broken links or unknown Mermaid fences fail the build.

## 6. Dependencies (uv)

Documentation tooling is isolated from the runtime members. Add a **PEP 735 dependency
group** at the workspace root in `pyproject.toml`:

```toml
[dependency-groups]
docs = ["mkdocs-material>=9.5"]
```

- `mkdocs-material` pulls `mkdocs` and `pymdown-extensions` transitively.
- It is a **dev/build group only** — never a dependency of `dsf-core`,
  `dsf-feature-council`, `dsf-cli`, or `dsf-control-center`, so import boundaries and the
  shipped packages are untouched.
- Local preview: `uv run --group docs mkdocs serve`.
- Local build: `uv run --group docs mkdocs build --strict`.

## 7. Deployment (GitHub Actions → Pages)

New workflow `.github/workflows/docs.yml`, with all third-party actions **pinned to commit
SHAs** (matching the convention in `ci.yml` and `agents-images.yml`).

- **Triggers:** `pull_request` (build-only guard) and `push` to `main` (build + deploy);
  plus `workflow_dispatch`. Path-filtered to `docs/site/**`, `mkdocs.yml`,
  `pyproject.toml`, and the workflow file.
- **`build` job (always):** checkout → `setup-uv` → `uv sync --group docs` →
  `uv run --group docs mkdocs build --strict` → `actions/upload-pages-artifact` (the
  built `site/`). On PRs this is the guard that catches broken links/Mermaid.
- **`deploy` job (main only):** `needs: build`, environment `github-pages`,
  `actions/deploy-pages`.
- **Permissions:** `contents: read`, `pages: write`, `id-token: write`.
- **Concurrency:** group `pages` with `cancel-in-progress: false` so deploys don't overlap.
- **One-time manual step (documented, not automatable here):** in repo Settings → Pages,
  set **Source = GitHub Actions**.

This is intentionally a **separate** workflow from `ci.yml` (ruff → lint-imports → pytest):
docs changes shouldn't trigger the test suite and vice-versa, and the Pages permissions
stay scoped to the docs workflow.

## 8. README after migration

The README stops being the concept explainer and becomes the **developer entry point**:

1. One short paragraph: what DSF is, with the loop Mermaid retained (it is the repo's
   signature visual and useful on the GitHub landing page).
2. A prominent link: **"Read the docs →"** to the Pages site for concept + how-to-use.
3. Build-focused content only: workspace layout (the four members), the canonical commands
   (`make install` / `make test` / `make lint` / `make lint-imports`), how to run the docs
   site locally, and a pointer to `docs/adr/` for architecture decisions.

No conceptual prose is duplicated between the README and the site — it lives on the site;
the README links to it.

## 9. Testing & success criteria

- `uv run --group docs mkdocs build --strict` passes with **zero** warnings (no broken
  internal links, all Mermaid fences recognised).
- The PR `build` job is green; merging to `main` deploys and the site is reachable at the
  project Pages URL.
- On the live site: Mermaid diagrams render, search works, the light/dark toggle works, and
  all nav entries resolve.
- A repo-wide grep shows **no** stale references to `docs/phases/`, `docs/RUNBOOK.md`, or
  `docs/GETTING_STARTED.md`; the README's getting-started links now resolve to the site.
- `docs/adr/` and `docs/superpowers/` are **absent** from the built site (verified by
  checking the generated `site/` has no `adr`/`superpowers` paths).
- Existing gates remain green and untouched: `uv run ruff check .`, `uv run lint-imports`,
  `uv run pytest -q` (the docs group adds no runtime imports).

## 10. Risks & mitigations

| Risk | Mitigation |
|---|---|
| Relocating files leaves dangling links | `--strict` build + repo-wide grep gate (section 4) |
| Mermaid doesn't render under Material | Wire `pymdownx.superfences` custom fence; verify the README diagram on the live loop page |
| Pages not deploying | One-time "Source = GitHub Actions" setting documented; `workflow_dispatch` to retrigger |
| Docs tooling leaks into runtime | Isolated `docs` dependency group; never added to member deps; CI import-linter unaffected |
| Concept drifts between README and site | README keeps no concept prose beyond the one-paragraph intro + loop diagram; single source on the site |

## 11. Out of scope (future, if wanted)

- Custom domain (`CNAME`) — default github.io URL for now.
- Versioned docs (`mike`) — single "latest" for now.
- Auto-generated code/API reference (`mkdocstrings`) — narrative-only by decision.
- A blog / changelog section.
