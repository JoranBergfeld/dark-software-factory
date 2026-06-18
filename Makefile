.PHONY: install test lint lint-imports fmt dryrun evals new-demo

install:
	uv sync --all-packages

test:
	uv run pytest -q

lint:
	uv run ruff check .

lint-imports:
	uv run lint-imports

fmt:
	uv run ruff check --fix .

dryrun:
	uv run dsfctl run --dry-run --signal tests/fixtures/sample_signal.json

evals:
	uv run python -m dsf.evals.runner --gate

new-demo:
	uv run dsf new --product demo --owner your-org --name-prefix demo
