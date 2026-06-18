.PHONY: install test lint fmt dryrun evals new-demo

install:
	uv sync

test:
	uv run pytest -q

lint:
	uv run ruff check .

fmt:
	uv run ruff check --fix .

dryrun:
	uv run dsfctl run --dry-run --signal tests/fixtures/sample_signal.json

evals:
	uv run python -m dsf.evals.runner --gate

new-demo:
	uv run dsf new --product demo --owner your-org --name-prefix demo
