# ADR-009 Legacy GymOS reference

Accepted. The legacy GymOS repository is only a read-only reference for calculations, tests and historical `.businessos.json` data. It is not the runtime of the new application. Python is not packaged with BusinessOS. Excel is not the source of truth. Approved GymOS logic will be ported later to C#. Compatibility will be checked with fixtures and differential tests. The `.businessos.json` format will be imported later as a legacy format.
