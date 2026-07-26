# Vault file format

WinARD currently writes vault format version 2. Each serialized manifest is
followed by a fresh 12-byte AES-GCM nonce and a 16-byte authentication tag.
The manifest nonce is authenticated by its tag and must not be the all-zero
verifier nonce or equal any entry nonce.

While a vault is open, WinARD also retains the manifest nonces it has emitted
and excludes them from subsequent entry and manifest nonce generation. A
cryptographic nonce source that returns a collision is retried and rejected if
it cannot produce a unique value within the bounded retry limit.

Format version 1 was never published. Version 2 intentionally rejects version
1 files instead of attempting an unauthenticated compatibility migration.
