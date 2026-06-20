.PHONY: install test lint lint-imports fmt

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
