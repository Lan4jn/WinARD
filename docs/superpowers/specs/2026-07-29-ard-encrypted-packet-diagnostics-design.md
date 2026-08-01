# WinARD ARD Encrypted Packet Diagnostics Design

Date: 2026-07-29

## Context

The macOS 26.5 diagnostic captured at 2026-07-29 19:33 shows that WinARD successfully negotiated RFB 003.889 shared control, processed an ARD Tickle, sent AutoFBUpdate, and then terminated while reading the next encrypted server message. The exported failure is limited to `ArdEncryptionPacket` at `ServerMessageType`.

That category currently combines outer packet length, CBC decryption, decrypted payload length, padding, and other framing failures. It therefore cannot distinguish whether the second and later server packets disagree with WinARD on sequence evolution, IV evolution, or packet layout. The first successful encrypted traffic makes account permissions, observation mode, initial TCP connection, and desktop input controls unlikely root causes.

## Goal

Add privacy-safe, deterministic diagnostics that identify the exact encrypted-packet validation stage and the non-secret transport position at failure. Produce a Release x64 artifact that requires only one normal connection attempt to collect the evidence needed for the protocol correction.

## Non-goals

- Do not try multiple IV or sequence algorithms automatically.
- Do not downgrade to plaintext or continue after any encrypted-packet failure.
- Do not add pointer smoke tests, coordinate scans, or synthetic input probes.
- Do not log keys, IVs, ciphertext, plaintext, hashes, framebuffer bytes, clipboard data, credentials, or host identity.
- Do not claim the continuous-packet protocol is fixed until a real macOS capture identifies and validates the rule.

## Chosen Approach

Introduce a structured encrypted-packet failure stage carried by `RfbProtocolFailureInfo`. Each rejection site in the encrypted stream and packet codec will report a distinct stage while retaining the existing high-level failure kinds.

The exported event may contain only:

- direction: receive or send;
- packet sequence number;
- outer ciphertext length when already parsed;
- encrypted-packet failure stage;
- existing RFB read context.

Payload length will not be exported in this diagnostic revision because it is derived only after decryption and is unnecessary to distinguish the current failure. No byte samples or cryptographic material will be retained.

## Failure Stages

The diagnostic model will distinguish at least:

1. `OuterLength`: zero, unaligned, or over-limit ciphertext length.
2. `TruncatedCiphertext`: end of stream before the declared ciphertext completes.
3. `CbcDecrypt`: the platform CBC primitive rejected the packet.
4. `PlaintextTooShort`: decrypted data cannot contain framing and digest.
5. `PayloadLength`: the declared payload leaves insufficient room for the digest.
6. `Padding`: non-zero bytes occur after the digest in the trailing block-alignment area.
7. `Integrity`: SHA-1 comparison failed.
8. `StateCommit`: the stream faulted or was disposed before sequence/IV state could commit.

Existing `ArdEncryptionIntegrity` remains the high-level kind for digest mismatch. Other stages remain `ArdEncryptionPacket`. This preserves current error presentation while making diagnostics actionable.

## Data Flow

`ArdEncryptedStream` supplies direction, current receive sequence, and parsed outer length when it invokes the codec. The codec creates protocol exceptions containing the precise stage. `RfbClient.ReceiveAsync` may add its existing server-message context without replacing the encryption fields. The desktop failure diagnostic exporter writes an allowlisted set of scalar fields.

No diagnostic path receives the key, IV, ciphertext buffer, decrypted buffer, or digest. Secret buffers continue to be cleared immediately on failure.

## Error Handling

All encrypted-packet errors remain fatal and fail closed. Diagnostic construction must not throw or replace the original protocol exception. If a stage or numeric value is unavailable, the exporter omits it instead of guessing.

Sequence and length values describe protocol position rather than content. Sequence is bounded to the existing unsigned packet counter, and ciphertext length is bounded by protocol limits.

## Testing

- Every encrypted-packet rejection path produces its expected stage.
- Context merging preserves encryption stage, direction, sequence, and ciphertext length when `ServerMessageType` is added later.
- Exported diagnostics contain the new safe scalar fields.
- Exported diagnostics do not contain keys, IVs, payloads, ciphertext, hashes, or exception text.
- Existing encryption, security-redaction, and desktop failure tests remain green.

Tests will follow red-green TDD. Fixed invalid packets will exercise each parser stage without adding production-only hooks.

## Manual Validation

1. Run the new Release x64 build normally against the same macOS 26.5 host.
2. Do not perform pointer scanning or protocol probes; simply allow the normal session to reach the disconnect.
3. Export diagnostics once.
4. Use the reported stage, direction, sequence, and length to select one protocol hypothesis.
5. Implement that single correction with a failing regression vector and produce the next build.

## Acceptance Criteria

- A diagnostic produced after the current disconnect identifies one exact encrypted-packet failure stage.
- It identifies the receive sequence and ciphertext length when available.
- It contains no cryptographic material or application payload.
- Strict Release build and the full automated test suite pass.
- A Release x64 ZIP is produced for the single normal macOS validation attempt.
