# ADR-001 New .NET foundation

## Status

Accepted

## Context

BusinessOS needs a Windows desktop foundation that is independent from the legacy Python GymOS codebase and does not require Excel at runtime.

## Decision

Use C# and .NET with a single repository solution, centralized package versions, nullable reference types, warnings as errors and deterministic build settings.

## Consequences

The foundation can be verified by the .NET SDK and developed with standard Windows tooling. Linux containers can inspect the code but cannot complete the WinUI desktop smoke test.

## Block 1 scope

Block 1 includes the solution skeleton, shared build settings, domain primitives, minimal module boundaries, unit tests, architecture tests and a minimal desktop shell. Persistence, audit, background jobs, backup and installers are out of scope.
