# ADR-005 Decimal money

## Status

Accepted

## Context

BusinessOS will eventually handle financial calculations where binary floating point would create unacceptable rounding errors.

## Decision

Represent monetary values with `decimal` in the `Money` value object and require an explicit `CurrencyCode`. Money can be added only when currencies match, and rounding uses a central policy.

## Consequences

The current foundation prevents accidental cross-currency addition and avoids `float`/`double` for money. Currency conversion and full financial engines remain future work.

## Block 1 scope

Block 1 contains value objects and unit tests for currency validation, same-currency addition, cross-currency rejection and rounding. It does not include exchange rates, accounting, budgets or financial reports.
