# Frozen Python parity baseline

This directory is the committed baseline for ticket #133.

It freezes observable Python/uv behavior for the .NET migration: command exits and output, machine-readable JSON, dry-run plans, contract schemas, request shapes, and persisted record examples.

Later .NET tests should consume these files directly and must not run Python to rediscover parity.

Use `matrix.json` for machine assertions and `matrix.md` for review.
