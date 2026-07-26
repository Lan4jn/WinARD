# Windows credential concurrency

`WindowsCredentialStore` serializes reads, writes, deletes, snapshots, and
compare-exchange operations with a process-wide asynchronous gate keyed by the
canonical Windows Credential Manager target name. This makes a snapshot plus
conditional mutation atomic among all `WindowsCredentialStore` instances in a
single WinARD process.

Each native credential blob is self-describing and contains a format marker,
format version, fresh 128-bit random revision, secret length, and the secret.
Snapshots expose only the revision as their opaque version token. Every save or
successful compare-exchange replacement generates a new revision, including
same-content rewrites, so process-local conditional writes detect ABA changes.
The header reduces the maximum stored secret from the native 2560-byte blob
limit to 2531 bytes.

The raw-blob format used during development was never released. WinARD rejects
legacy or malformed native blobs instead of interpreting unauthenticated bytes
as a credential. A future released-format migration must be explicit and
version-aware.

Windows Credential Manager does not expose a native compare-exchange operation
used by WinARD. The implementation therefore reads the current credential and
performs the conditional write or delete while holding only the WinARD process
gate. Another process can still change the same native credential between that
read and mutation.

Consequently, process-local migration recovery is protected from concurrent
WinARD writers, but cross-process atomic migration remains an architecture and
release gate. Callers must treat target-write uncertainty as unresolved when an
external credential writer may be active.

The process-wide target-gate dictionary currently retains canonical target
names for the process lifetime. Gate reclamation is deferred because removing a
gate safely requires proving that no waiter can still acquire the retired
instance; this bounded process-lifetime metadata does not contain credentials.
