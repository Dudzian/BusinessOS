# Architecture overview

BusinessOS is a modular monolith. Presentation depends on Application; Application depends on Domain; Infrastructure implements Application contracts and persists Domain models. Domain projects must not reference EF Core, SQLite, WinUI, Python or Excel libraries.
