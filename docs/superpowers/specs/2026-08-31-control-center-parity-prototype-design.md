# Control Center parity prototype design

## Purpose

This throwaway prototype resolves how the .NET Control Center should expose authenticated
operator policy controls. It does not implement the migration.

## Operator surface

The primary view is scoped to one selected product. It shows that product's effective:

- critic and source-agent enablement
- trigger pause state
- confidence threshold and critic weights

The global dry-run emergency switch is separate from the product policy view. Its active state
is conspicuous, and changing it requires explicit confirmation.

## Authentication and writes

Browser writes use a server-issued secure session cookie and a CSRF token. Bearer
authentication remains available only to automated API clients.

The UI permits edits only to an allowlisted set of numeric policy fields. Inputs validate known
ranges before save, and each control displays its effective value. Unsupported writes stay
visible but disabled, explaining both why they are unavailable and the supported alternative.

## Prototype behavior

The prototype is a switchable UI mockup that holds all state in memory. It renders the complete
effective product policy after every interaction so an operator can evaluate scope, safety, and
feedback without any runtime dependency or persistence.

## Out of scope

The prototype does not implement .NET services, authentication middleware, persistence,
production API endpoints, migration logic, or tests.
