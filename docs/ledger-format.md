# Ledger compatibility

The current ledger schema is 1. Existing records without an explicit schema version are read as schema 1. Unknown versions fail closed and are never rewritten as empty state. Only HTTP 204 denotes an absent ledger.
