# Universal Agent Instructions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give Claude and GitHub Copilot identical repository guidance from root `AGENTS.md`.

**Architecture:** Root `AGENTS.md` becomes the single source of repository instructions.
Claude loads it through `CLAUDE.md`; Copilot loads it directly. Existing agent-skill
configuration remains in `docs/agents/` and is linked from the canonical file.

**Tech Stack:** Markdown, Claude Code instruction imports, GitHub Copilot repository
instructions, Git.

---

## File Structure

- Create: `AGENTS.md` — canonical repository guidance for all coding agents.
- Modify: `CLAUDE.md` — Claude compatibility loader containing only `@AGENTS.md`.
- Delete: `.github/copilot-instructions.md` — superseded Copilot-specific duplicate.
- Preserve: `docs/agents/issue-tracker.md` — GitHub issue workflow.
- Preserve: `docs/agents/triage-labels.md` — canonical triage label mapping.
- Preserve: `docs/agents/domain.md` — single-context domain documentation rules.

### Task 1: Establish the canonical instruction file

**Files:**
- Create: `AGENTS.md`
- Modify: `CLAUDE.md`
- Delete: `.github/copilot-instructions.md`

- [ ] **Step 1: Move the complete Copilot guidance to the universal location**

Use `apply_patch` to move `.github/copilot-instructions.md` to `AGENTS.md` without changing
its body:

```text
*** Begin Patch
*** Update File: .github/copilot-instructions.md
*** Move to: AGENTS.md
@@
-# Copilot instructions — Dark Software Factory (DSF)
+# Coding agent instructions — Dark Software Factory (DSF)
*** End Patch
```

Expected: `AGENTS.md` contains the former Copilot guidance, and
`.github/copilot-instructions.md` no longer exists.

- [ ] **Step 2: Add the approved agent-skill pointers**

Append this exact block to `AGENTS.md`:

```markdown
## Agent skills

### Issue tracker

Issues are tracked in this repository's GitHub Issues. See `docs/agents/issue-tracker.md`.

### Triage labels

Triage uses the five canonical label names. See `docs/agents/triage-labels.md`.

### Domain docs

Domain documentation uses a single-context layout. See `docs/agents/domain.md`.
```

Expected: every coding agent can discover the issue tracker, label vocabulary, and domain
documentation rules from the canonical instructions.

- [ ] **Step 3: Replace Claude-specific duplication with the compatibility import**

Replace all content in `CLAUDE.md` with:

```markdown
@AGENTS.md
```

Expected: Claude loads the same root guidance as Copilot without maintaining a second copy.

- [ ] **Step 4: Verify the instruction topology**

Run:

```bash
test -f AGENTS.md
test "$(cat CLAUDE.md)" = "@AGENTS.md"
test ! -e .github/copilot-instructions.md
grep -q '^## Agent skills$' AGENTS.md
grep -q 'docs/agents/issue-tracker.md' AGENTS.md
grep -q 'docs/agents/triage-labels.md' AGENTS.md
grep -q 'docs/agents/domain.md' AGENTS.md
test -f docs/agents/issue-tracker.md
test -f docs/agents/triage-labels.md
test -f docs/agents/domain.md
git diff --check
```

Expected: exit code 0 with no output.

- [ ] **Step 5: Inspect the complete scoped diff**

Run:

```bash
git --no-pager diff -- AGENTS.md CLAUDE.md .github/copilot-instructions.md docs/agents
git status --short -- AGENTS.md CLAUDE.md .github/copilot-instructions.md docs/agents
```

Expected: one canonical `AGENTS.md`, one-line `CLAUDE.md`, deleted Copilot duplicate, and
the three approved `docs/agents/*.md` files. No unrelated paths appear.

- [ ] **Step 6: Commit the universal instructions**

Run:

```bash
git add AGENTS.md CLAUDE.md .github/copilot-instructions.md \
  docs/agents/issue-tracker.md docs/agents/triage-labels.md docs/agents/domain.md
git commit -m "docs: make agent instructions universal" \
  -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>" \
  -m "Copilot-Session: dd85a9f1-7478-49fd-bdac-f37e260bfe51"
```

Expected: commit succeeds and includes only the six scoped instruction paths.
