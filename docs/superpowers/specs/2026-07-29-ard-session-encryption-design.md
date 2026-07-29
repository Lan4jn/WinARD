# WinARD ARD Session Encryption Design

Date: 2026-07-29

## Context

WinARD can authenticate to macOS 26.5 with Apple Remote Desktop security type 30, enter shared control mode, render the remote framebuffer, and keep the session alive. The target Mac identifies the connection as an assistance/control session, but mouse and keyboard input are ignored.

The current implementation derives a 16-byte MD5 authentication key during the type 30 Diffie-Hellman exchange, uses it only to encrypt the credentials, and then clears it. Session initialization does not advertise ARD Session Encryption pseudo-encoding 1103 or send SetEncryption (client message type 0x12). Keyboard input is sent as an ordinary RFB KeyEvent (type 0x04), and all later traffic remains plaintext.

The reference ARD client retains the type 30 authentication key. Its default mode requests full session encryption before the first framebuffer update. This missing shared input and transport layer explains why both input classes fail despite successful control-mode negotiation.

## Goal

Implement the full ARD session-encryption negotiation and encrypted transport used by macOS 26.5 so that all post-negotiation protocol traffic, including mouse and keyboard input, is carried through the negotiated encrypted stream.

Automated verification establishes protocol correctness only. Successful control of a real Mac remains a required manual acceptance test.

## Non-goals

- Do not add a user-selectable encryption mode in this change.
- Do not implement a keystroke-only fallback or silently downgrade to plaintext.
- Do not add pointer smoke tests, coordinate scans, or synthetic remote-control claims.
- Do not redesign viewport input handling, clipboard semantics, or framebuffer codecs unless encryption integration exposes a concrete defect.
- Do not log credentials, keys, IVs, plaintext input, clipboard contents, or decrypted framebuffer data.

## Chosen Approach

WinARD will support full-session encryption as the single ARD control transport. The type 30 authentication key will have an explicit bounded lifetime, initialization will request encryption, the server's session material will be consumed from pseudo-encoding 1103, and the connection will switch atomically from plaintext RFB framing to ARD encrypted packets after acknowledging the negotiation.

Alternatives considered:

1. Keystroke-only EncryptedEvent (0x10) was rejected because it does not account for the failed pointer path.
2. Full encryption with automatic fallback was deferred because it adds downgrade behavior and a second runtime state machine before the primary macOS 26.5 path has been validated.

## Architecture

### Authentication material

`ArdAuthenticator` will return an authentication result that owns the 16-byte key derived as `MD5(sharedSecret)`. Temporary DH values, credential plaintext, credential ciphertext, and intermediate arrays remain zeroed as today. Ownership of the authentication key transfers to the live client/session object, which clears it during disposal and every failed initialization path.

The key must not be exposed through diagnostics, public view-model state, exception messages, or general-purpose configuration objects.

### Initialization and negotiation

For ARD 3.889 sessions, the requested encoding list will include pseudo-encoding 1103. After ViewerInfo, SetMode, SetDisplay, pixel format, and final encodings are declared, WinARD will send a plaintext SetEncryption request before the first framebuffer update request.

The request format is:

```text
type=0x12, pad=0, command=u16be(1), level=u16be(1),
methodCount=u16be(1), method=u32be(1)
```

The normal ARD framebuffer decoder will recognize encoding 1103. Its 36-byte payload is:

```text
version=u32be(1), encryptedSessionKey[16], encryptedIV[16]
```

The two encrypted fields are decrypted independently with AES-128-ECB using the type 30 authentication key. An unsupported version, missing authentication key, or malformed payload is a fatal protocol error.

Encryption activation is deferred until every rectangle in the current framebuffer update has been consumed. WinARD then writes the plaintext SetEncryption acknowledgement and waits for that write to complete:

```text
type=0x12, pad=0, command=u16be(2), level=u16be(1),
methodCount=u16be(0)
```

Only after this exact write boundary may the shared transport enter encrypted mode. No later message may bypass that transport.

### Encrypted transport

The transport layer, rather than individual message writers, owns encryption. Pointer, keyboard, clipboard, framebuffer request, AutoFBUpdate, and every other client message continue to produce ordinary RFB bytes and write them through the same serialized writer.

Each encrypted packet uses this wire form:

```text
u16be(ciphertextLength) || AES-128-CBC(ciphertext)
```

Before encryption, the packet plaintext is built as:

```text
u16be(payloadLength) || payload || zeroPadding || sha1[20]
```

The total plaintext length is the smallest multiple of 16 that can hold the two-byte length, payload, padding, and 20-byte digest. Payload and ciphertext lengths are bounded by protocol limits and the two-byte wire length.

The digest is SHA-1 over:

```text
u32be(directionSequence) || plaintextWithoutFinalDigest
```

Send and receive directions maintain independent sequence counters and chaining IVs. After each packet, the next IV is the last ciphertext block from that direction. Counters begin at zero when encryption activates and increment exactly once per successfully processed packet.

The existing connection-wide serialized write path must cover packet construction, sequence allocation, CBC encryption, wire write, and IV advancement. Concurrent pointer, keyboard, Tickle response, and update-request writes must never reuse a sequence number or IV.

Incoming TCP data may split or combine encrypted packets. The encrypted reader accumulates bytes until the two-byte length and complete ciphertext are available, authenticates and decrypts one packet at a time, then exposes only the declared RFB payload to the existing protocol reader.

### Runtime input

After full-session encryption activates, existing standard PointerEvent and KeyEvent messages are carried inside encrypted packets. Pointer movement remains the standard six-byte RFB message, and no coordinate probing or speculative alternate pointer message is introduced.

The ARD right/middle button ordering difference is separate from the current total input failure. It may be corrected as a small mapping change if not already present, but it is not evidence for or a substitute for encryption support.

## State Model

The connection has four encryption states:

1. `Plaintext`: authenticated but no encryption request has been sent.
2. `Requested`: SetEncryption command 1 was sent; pseudo-encoding 1103 is expected.
3. `PendingActivation`: valid session key and IV were decrypted; the current framebuffer update is still being consumed.
4. `Encrypted`: plaintext acknowledgement completed and all subsequent transport reads and writes use encrypted packets.

State transitions are one-way. Duplicate or out-of-order encryption material, a second activation, or ordinary plaintext traffic observed after activation is a protocol failure. Disposal is permitted from every state and clears all owned secret material.

## Error Handling and Diagnostics

The connection terminates with a specific protocol failure when any of these conditions occurs:

- pseudo-encoding 1103 has an unsupported version or invalid length;
- session key or IV cannot be decrypted;
- encrypted packet length is zero, not a multiple of 16, exceeds limits, or cannot contain the mandatory framing and digest;
- declared payload length extends into padding or the digest;
- CBC decryption fails;
- SHA-1 validation fails;
- sequence or activation state is invalid;
- a transport read or write fails during the plaintext-to-encrypted boundary.

Continuing after an integrity failure is forbidden because losing one encrypted packet also loses the sequence and CBC chaining state.

Safe diagnostics may include the encryption state, direction, sequence number, ciphertext length, payload length, and failure category. They must not include cryptographic material or application payload bytes.

All secret arrays and buffered decrypted plaintext are cleared on normal disconnect, cancellation, initialization failure, integrity failure, and disposal.

## Testing

### Cryptographic unit tests

- Fixed vectors for AES-128-ECB session key and IV decryption.
- Fixed vectors for CBC packet encryption and decryption.
- Fixed vectors for SHA-1 input construction and digest verification.
- Boundary cases for payload size, block alignment, and two-byte lengths.
- Verification that owned secret buffers are cleared on disposal and failure paths.

### Transport tests

- Fragmented two-byte header, fragmented ciphertext, and multiple packets in one read.
- Independent send and receive sequence and IV evolution across consecutive packets.
- Rejection of invalid lengths, malformed payload lengths, truncated ciphertext, and digest mismatch.
- Concurrent writes serialize into distinct packets with monotonically increasing sequence numbers.
- Cancellation and socket failures do not advance transport state inconsistently.

### Negotiation and integration tests

- ARD encodings contain 1103 and initialization sends SetEncryption command 1 before the first framebuffer request.
- A 1103 rectangle is fully consumed without prematurely decrypting remaining rectangles as encrypted transport.
- Completion of that framebuffer update sends plaintext command 2 before switching transport state.
- PointerEvent, KeyEvent, AutoFBUpdate, Tickle response, clipboard message, and framebuffer request are encrypted after activation and do not appear as plaintext on the underlying stream.
- Existing ARD session-selection, display bootstrap, state-change, framebuffer decoding, and remote-close tests remain green.

### Manual acceptance on macOS 26.5

1. Connect with an account that has Observe and Control permissions.
2. Confirm macOS reports an assistance/control session rather than observation-only mode.
3. Leave the connection idle long enough to confirm it remains connected.
4. Move the pointer, left-click, drag, and right-click.
5. Type printable text and common modifier combinations.
6. Confirm framebuffer updates and input continue together without disconnect or corruption.
7. Export diagnostics after success and after any failure to verify that no sensitive material is present.

Until this manual acceptance succeeds, the implementation must not be described as proven remote-control support.

## Delivery Boundary

The implementation is complete when the negotiation, encrypted transport, lifecycle cleanup, diagnostics, and automated tests described above are present and passing, and a Release x64 artifact is produced for real-Mac validation. Actual macOS control remains pending until the user completes the manual acceptance steps.
