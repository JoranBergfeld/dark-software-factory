.PHONY: install test lint fmt dryrun evals

install:
	uv venv --python 3.12
	uv pip install -e ".[dev]"

test:
	uv run pytest -q

lint:
	uv run ruff check .

fmt:
	uv run ruff check --fix .

dryrun:
	uv run python -m dsf.cli run --dry-run --signal tests/fixtures/sample_signal.json

evals:
	uv run python -m dsf.evals.runner --gate
