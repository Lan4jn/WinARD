# Windows credential concurrency

`WindowsCredentialStore` serializes reads, writes, deletes, snapshots, and
compare-exchange operations with a process-wide asynchronous gate keyed by the
canonical Windows Credential Manager target name. This makes a snapshot plus
conditional mutation atomic among all `WindowsCredentialStore` instances in a
single WinARD process.

Windows Credential Manager does not expose a native compare-exchange operation
used by WinARD. The implementation therefore reads the current credential and
performs the conditional write or delete while holding only the WinARD process
gate. Another process can still change the same native credential between that
read and mutation.

Consequently, process-local migration recovery is protected from concurrent
WinARD writers, but cross-process atomic migration remains an architecture and
release gate. Callers must treat target-write uncertainty as unresolved when an
external credential writer may be active.
