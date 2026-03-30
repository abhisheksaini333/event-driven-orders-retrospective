# Ledger compatibility

The current ledger schema is 1. Existing records without an explicit schema version are read as schema 1. Unknown versions fail closed and are never rewritten as empty state. Only HTTP 204 denotes an absent ledger.

Snapshot verification requires the capture metadata written by export: a bounded source key, positive canonical Redis source version and an offset-aware capture timestamp. Handwritten envelopes lacking these fields are rejected before restore.
