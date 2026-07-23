# ADR-002 Modular monolith

## Status

Accepted

## Context

BusinessOS must grow into a product with a shared core and future modules without introducing distributed-system complexity in the first foundation block.

## Decision

Use a modular monolith with separate projects for BuildingBlocks and modules. Domain projects remain framework-independent; application projects expose registration extension points; infrastructure projects may remain as empty boundaries until their implementation block.

## Consequences

The codebase has explicit module seams without microservices or runtime plugin loading. Architecture tests protect basic dependency boundaries.

## Block 1 scope

Block 1 defines module boundaries for Companies, BusinessProjects, Budgeting and Core, but does not implement persistence, UI workflows for companies/projects, background jobs or integrations.
