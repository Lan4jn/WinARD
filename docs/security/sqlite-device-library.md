# SQLite device library security boundary

The device library stores connection metadata and opaque credential references only. It must never store password bytes, private-key passphrases, OpenSSH AskPass authentication tokens, or other secret material. SSH host-key public key bytes are trust metadata and are stored with their algorithm, fingerprint, and canonical endpoint.

`WinArdDatabase` creates the parent directory and database but does not claim to harden their Windows ACLs. Task 16 packaging must place the database below the per-user application-data directory and apply/verify a user-only ACL before release. Callers must not select a shared or world-writable path.

Every opened connection enables foreign keys, WAL mode, and a bounded busy timeout. Schema migration uses an immediate SQLite transaction, rejects future versions, and requires exactly one `schema_version` row. Repository operations use parameterized SQL and per-operation connections.

The current `net8.0-windows10.0.19041.0` SDK projection does not expose the DNS-SD `DeviceWatcher` bridge required for reliable add/update/remove events. The production `DnssdServiceWatcher` is therefore compile-gated by `WINARD_DNSSD_DEVICE_WATCHER` and fails closed when that adapter is unavailable; it does not substitute fake discovery.
