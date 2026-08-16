# Supplier Invoices (Block 3B7)

Block 3B7 provides a manual register of supplier cost invoices assigned to a business project. The supplier is free text and each entry records an invoice number, one positive invoice amount in the project's base currency, invoice date, due date, and an optional note.

Invoice identity is `(project, trimmed uppercase supplier, trimmed uppercase invoice number)`. It remains unique after soft archive. Updates and archives use an incrementing version for optimistic concurrency.

This slice deliberately has no VAT split, payment status, bank or reconciliation behavior, vendor master data, attachment/file ingestion, OCR, or posting/conversion to Actual Cost. Invoices remain independent of Actual Cost and Forecast Cost.
