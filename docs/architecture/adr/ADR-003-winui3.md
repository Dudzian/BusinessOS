# ADR-003 WinUI 3 desktop shell

## Status

Accepted

## Context

BusinessOS is intended to be a professional Windows desktop application installed and used locally.

## Decision

Use WinUI 3 with Windows App SDK for the desktop UI. Block 1 builds an unpackaged x64 Windows app with a single `BusinessOS` window and a view model supplied through the host/DI composition root.

## Consequences

The UI is Windows-specific and is not portable to Linux or macOS. Full verification requires a Windows runner capable of building and launching WinUI apps. MSIX packaging is intentionally deferred to a later block.

## Block 1 scope

Block 1 contains only the minimal shell, smoke-test automation and no navigation, company UI, project UI, backup UI, audit UI or dashboard.
