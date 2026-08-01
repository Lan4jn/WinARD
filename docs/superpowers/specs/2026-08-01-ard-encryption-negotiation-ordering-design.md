# ARD Session Encryption Negotiation Ordering Design

Date: 2026-08-01

## Context

The macOS 26.5 diagnostic captured on 2026-08-01 shows successful ARD 3.889 shared-control negotiation followed by a normal server `FramebufferUpdate` (`0x00`). WinARD then terminates with `ArdEncryptionNegotiation` because its current state machine requires pseudo-encoding 1103 to appear in the first completed framebuffer update after SetEncryption command 1.

That ordering requirement is a WinARD assumption, not a protocol boundary. macOS may already have an ordinary framebuffer update queued when it processes the SetEncryption request. The 1103 rectangle can therefore arrive in a later update.

## Goal

Allow ordinary plaintext framebuffer updates to precede the 1103 session-material rectangle without disconnecting, while ensuring pointer, keyboard, and clipboard data can never be sent plaintext during the negotiation window.

## Non-goals

- Do not add plaintext fallback or a user-selectable downgrade.
- Do not resend SetEncryption automatically or try alternate negotiation formats.
- Do not add a new protocol timeout in this change; existing connection cancellation and transport failure remain authoritative.
- Do not change encrypted-packet framing, sequence counters, IV chaining, or cryptographic algorithms.
- Do not log keys, IVs, encrypted material, input payloads, or framebuffer bytes.

## Considered Approaches

### 1. Accept later 1103 and gate sensitive writes (selected)

Keep the state `Requested` across any number of valid ordinary framebuffer updates. Only a valid 1103 rectangle moves the state to `PendingActivation`; completion of that same update sends the plaintext acknowledgement and atomically activates encryption. Pointer, key, and clipboard writes wait for activation before reaching the transport.

This matches the observed macOS ordering, preserves fail-closed input handling, and makes no speculative protocol changes.

### 2. Keep failing after the first update

This is the current behavior and is rejected because the real server has demonstrated that an ordinary framebuffer update may arrive first.

### 3. Resend SetEncryption or automatically vary the request

This is rejected because there is no evidence that the request is malformed or lost. Retrying or changing wire formats would add unverified protocol behavior and make diagnostics ambiguous.

## State and Data Flow

1. `RequestAsync` sends SetEncryption command 1 and enters `Requested`.
2. A completed framebuffer update without 1103 leaves the state at `Requested` and is otherwise processed normally.
3. Framebuffer update requests and required ARD liveness replies may remain plaintext while negotiation is pending so the server can deliver the later 1103 response.
4. Pointer, keyboard, and clipboard writers wait on a single activation completion signal. They do not write any bytes while the state is `Requested` or `PendingActivation`.
5. A valid 1103 rectangle decrypts the session key and IV and enters `PendingActivation`.
6. Completion of that framebuffer update writes SetEncryption command 2, activates `ArdEncryptedStream`, enters `Encrypted`, and releases waiting sensitive writers. Their writes then pass through the encrypted stream.
7. Disposal, cancellation, malformed 1103, transport failure, or connection closure remains terminal. Waiting writers are released by cancellation or disposal rather than falling back to plaintext.

The activation signal uses asynchronous continuations and is completed only after the acknowledgement write and transport activation both succeed.

## Error Handling

- Unsupported 1103 versions, malformed rectangle dimensions, duplicate material, or out-of-order material remain fatal negotiation failures.
- An ordinary framebuffer update while `Requested` is valid and no longer creates an error.
- Input waiting for activation observes its own cancellation token.
- Disposing the negotiation controller before activation releases waiters with an object-disposed failure.
- No secret material or payload data is added to diagnostics.

## Verification

- A unit test proves the first ordinary framebuffer update leaves the controller in `Requested` without encrypting the transport.
- A second update containing 1103 transitions through `PendingActivation` to `Encrypted`.
- Pointer, key, and clipboard writes started before activation produce no transport bytes until activation completes, then decode correctly as encrypted packets with sequential IV chaining.
- Existing malformed and duplicate 1103 tests remain fail-closed.
- Full Release build runs with warnings as errors and the complete solution test suite passes.
- Real macOS 26.5 validation remains required; automated tests do not establish real-host control success.
