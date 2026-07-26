# SQLite device library security boundary

The device library stores connection metadata and opaque credential references only. It must never store password bytes, private-key passphrases, OpenSSH AskPass authentication tokens, or other secret material. SSH host-key public key bytes are trust metadata and are stored with their algorithm, fingerprint, and canonical endpoint.

`WinArdDatabase` creates the parent directory and database but does not claim to harden their Windows ACLs. Task 16 packaging must place the database below the per-user application-data directory and apply/verify a user-only ACL before release. Callers must not select a shared or world-writable path.

Every opened connection enables foreign keys, WAL mode, and a bounded busy timeout. Schema migration uses an immediate SQLite transaction, rejects future versions, and requires exactly one `schema_version` row. Repository operations use parameterized SQL and per-operation connections.

Bonjour discovery uses `DeviceInformation.CreateWatcher` with `DeviceInformationKind.AssociationEndpointService`. The AQS limits enumeration to the Windows DNS-SD provider (`{4526e8c1-8aac-4153-9b16-55e86ada0e54}`) and `_rfb._tcp`. The provider GUID and AEP Service protocol-property syntax come from Microsoft's [Enumerate devices over a network](https://learn.microsoft.com/windows/apps/develop/devices-sensors/enumerate-devices-over-a-network) documentation.

The requested property names are registered Windows Property System canonical names: `System.Devices.Dnssd.ServiceName`, `InstanceName`, `Domain`, `HostName`, `PortNumber`, `TextAttributes`, `NetworkAdapterId`, plus `System.Devices.IpAddress` as an address fallback. The Windows property descriptions define the relevant boxed types as strings, `UInt16`, string vectors, and a GUID. Parsing remains defensive because `DeviceInformationUpdate` contains partial property sets and WinRT projection shapes can vary.

Real network discovery remains an external integration gate: release validation must browse an advertised `_rfb._tcp` service on each supported Windows build and verify add, update, removal, interface changes, TXT records, IPv4, IPv6, and stale-service expiry. Non-Windows execution fails closed before creating a watcher.
