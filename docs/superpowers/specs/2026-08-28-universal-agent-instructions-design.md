# Universal Agent Instructions Design

## Goal

Give Claude and GitHub Copilot the same repository guidance from one authoritative file.

## Architecture

Root `AGENTS.md` becomes the canonical instruction document. It contains the merged
repository guidance currently split between `CLAUDE.md` and
`.github/copilot-instructions.md`, including the pointers under `## Agent skills`.

Claude Code loads the canonical guidance through a minimal `CLAUDE.md` containing:

```markdown
@AGENTS.md
```

GitHub Copilot loads root `AGENTS.md` directly. The redundant
`.github/copilot-instructions.md` is removed so repository guidance has one source of truth.

## Merge Rules

- Start from the more complete Copilot instructions.
- Preserve useful guidance unique to `CLAUDE.md`.
- Preserve the approved `## Agent skills` section and its `docs/agents/*.md` pointers.
- Resolve conflicting examples in favor of paths that exist in the current repository.
- Keep repository-specific rules in `AGENTS.md`; keep tool-specific compatibility only in
  the minimal loader file.

## Agent Flow

1. Copilot discovers `AGENTS.md` and reads repository guidance directly.
2. Claude discovers `CLAUDE.md`, follows `@AGENTS.md`, and reads the same guidance.
3. Both agents follow pointers to `docs/agents/issue-tracker.md`,
   `docs/agents/triage-labels.md`, and `docs/agents/domain.md` when those branches apply.

## Failure Prevention

The design avoids duplicated instruction bodies, which can drift or conflict. The Claude
loader names one local file and introduces no conditional behavior or fallback.

## Validation

- `AGENTS.md` contains every required repository section and the Agent skills pointers.
- `CLAUDE.md` contains only the `@AGENTS.md` import.
- `.github/copilot-instructions.md` no longer exists.
- `git diff --check` reports no whitespace errors.

No runtime code changes, so code tests and builds are unnecessary.
