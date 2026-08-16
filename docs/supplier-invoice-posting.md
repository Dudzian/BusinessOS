# Supplier Invoice Posting (Block 3B8)

Block 3B8 adds optional, manual, one-way posting of an active supplier invoice to an Actual Cost. The operator explicitly selects CAPEX or OPEX; project, amount, currency, incurred date, note, and the `Faktura <invoice number>` name are copied deterministically from the invoice.

Posting is exactly once and atomic: one database unit of work inserts the Actual Cost and marks the invoice with its linked cost and UTC posting timestamp. The invoice remains active and visible, but becomes immutable. There is no unpost or synchronization. A linked Actual Cost remains an ordinary cost and may later be edited or archived independently without making the invoice postable again.

Posting neither matches, updates, nor realizes Forecast Costs. This block does not add payments, VAT/tax accounting, OCR/file ingestion, attachments, purchase orders, or banking.
