# Apple Remote Desktop RFB security type 30

This note records the interoperability evidence used for WinARD's independent implementation of Apple Remote Desktop (ARD) RFB security type 30. No source code from the referenced implementations was copied.

## Sources checked

| Source | License | Protocol facts observed |
| --- | --- | --- |
| [LibVNC/LibVNCClient `HandleARDAuth`](https://github.com/LibVNC/libvncserver/blob/42494999e6492aaab9c1db785ecd293ef10b3aed/src/libvncclient/rfbclient.c#L800-L921), including its [OpenSSL crypto adapter](https://github.com/LibVNC/libvncserver/blob/42494999e6492aaab9c1db785ecd293ef10b3aed/src/common/crypto_openssl.c#L162-L270) | GPL-2.0-or-later; the file header and repository [license](https://github.com/LibVNC/libvncserver/blob/42494999e6492aaab9c1db785ecd293ef10b3aed/COPYING) apply. We used it only as behavioral documentation. | Reads a two-byte generator, a big-endian two-byte key length, then `keyLength` bytes each for the modulus and server public key. Produces fixed-width DH values, hashes the fixed-width shared secret with MD5, packs two 64-byte credential fields into a random 128-byte plaintext, encrypts with AES-128-ECB with padding disabled, then writes ciphertext before the client public key. |
| [noVNC ARD negotiation](https://github.com/novnc/noVNC/blob/7c36fabe599e053c5a81e98e091ac636f6c1e174/core/rfb.js#L1807-L1870), its [DH implementation](https://github.com/novnc/noVNC/blob/7c36fabe599e053c5a81e98e091ac636f6c1e174/core/crypto/dh.js), and [AES-ECB adapter](https://github.com/novnc/noVNC/blob/7c36fabe599e053c5a81e98e091ac636f6c1e174/core/crypto/aes.js#L1-L31) | noVNC core is MPL-2.0; see its [license notice](https://github.com/novnc/noVNC/blob/7c36fabe599e053c5a81e98e091ac636f6c1e174/LICENSE.txt). | Independently confirms the same challenge layout, fixed `keyLength` public/shared values, `MD5(sharedSecret)`, 128 random credential bytes with username at offset 0 and password at offset 64, AES-ECB, and response order of encrypted credentials followed by the client public key. |
| [noVNC protocol-version negotiation](https://github.com/novnc/noVNC/blob/7c36fabe599e053c5a81e98e091ac636f6c1e174/core/rfb.js#L1503-L1546) and its [003.889 test](https://github.com/novnc/noVNC/blob/7c36fabe599e053c5a81e98e091ac636f6c1e174/tests/test.rfb.js#L1421-L1424) | MPL-2.0 for the noVNC core implementation. | Explicitly identifies `003.889` as Apple Remote Desktop, maps it to RFB 3.8, and therefore sends the normalized client banner `RFB 003.008\n`. |
| [LibVNCServer macOS minor-version 889 handling](https://github.com/LibVNC/libvncserver/blob/42494999e6492aaab9c1db785ecd293ef10b3aed/src/libvncserver/auth.c#L168-L207) | GPL-2.0-or-later; used only as behavioral documentation. | Independently identifies protocol minor version 889 as a special value used by the built-in macOS VNC client. |
| [RFC 6143, RFB SecurityResult and version differences](https://www.rfc-editor.org/rfc/rfc6143.html#section-7.1.3) | IETF Trust legal provisions for RFC documents. | Defines the big-endian 32-bit `SecurityResult`; zero is success. RFB 3.8 failure carries a length-prefixed reason. Appendix A records that RFB 3.3 and 3.7 authentication failure does not carry the later reason string. |

The two public ARD client implementations agree on the wire order and cryptographic transformations relevant to this task. No conflict requiring an implementation choice was found.

## Apple 003.889 banner compatibility

WinARD accepts only the exact server banner `RFB 003.889\n` as an Apple compatibility alias. It normalizes the negotiated version to RFB 3.8, writes `RFB 003.008\n` back to the server, and then uses the RFB 3.8 security-type list and failure-reason rules. Other unknown 3.x banners remain unsupported.

## Confirmed wire sequence

After RFB security type 30 has been selected:

1. Server sends `generator` as an unsigned big-endian `u16`.
2. Server sends `keyLength` as an unsigned big-endian `u16`.
3. Server sends the unsigned big-endian DH modulus in exactly `keyLength` bytes.
4. Server sends its unsigned big-endian DH public key in exactly `keyLength` bytes.
5. Client creates a DH private exponent and computes its public key and shared secret. Both transmitted/hashed integer values are serialized unsigned, big-endian, and left-padded with zero bytes to exactly `keyLength` bytes.
6. Client derives the 16-byte AES key as `MD5(fixedWidthSharedSecret)`.
7. Client fills a 128-byte plaintext with cryptographically secure random bytes. It writes the UTF-8 username at offsets 0 through 62 and a NUL at the next byte. It writes the UTF-8 password at offsets 64 through 126 and a NUL at the next byte.
8. Client encrypts all 128 bytes using AES-128 in ECB mode with no padding.
9. Client writes the 128 encrypted credential bytes, followed immediately by the fixed-width client public key.
10. Server sends a big-endian `u32` RFB `SecurityResult`.

## WinARD implementation decisions

- MD5 and AES-ECB are legacy protocol compatibility requirements, not modern cryptographic choices. Analyzer suppression is limited to the exact protocol calls. The implementation must not reuse these primitives elsewhere.
- The first release accepts `keyLength` from 64 through 512 bytes. Values outside this range are rejected after reading the four-byte header and before allocating challenge payload buffers.
- The modulus encoding must occupy the negotiated unsigned width (no leading zero byte), be positive, and be odd. The generator must be at least 2. The server public key must be in the inclusive range 2 through `p - 2`.
- WinARD does not copy the truncation behavior seen in the reference clients. Username and password are each limited to 63 UTF-8 bytes and are rejected before any response write if too long. Invalid UTF-8 and embedded NUL bytes are also rejected, preventing the server from silently interpreting a different credential.
- The DH private exponent is selected without modulo bias by rejection-sampling the inclusive range 2 through `p - 2`. Sampling stops after 128 rejected candidates so a faulty or malicious random source cannot cause an infinite loop.
- The private-exponent byte buffer, fixed-width shared secret, AES key, credential plaintext, copied username/password, and raw failure-reason buffers are cleared with `CryptographicOperations.ZeroMemory` in `finally`/`Dispose` paths. The implementation uses .NET `BigInteger` only with `isUnsigned: true` and `isBigEndian: true`.
- Authentication never flushes or disposes the caller's stream. Reads and writes go through the existing bounded RFB reader/writer abstractions.
- On RFB 3.8 failure, WinARD fully consumes the bounded reason payload, replaces every exact username/password UTF-8 byte sequence with `[REDACTED]` before decoding, decodes invalid UTF-8 with replacement characters, escapes unsafe display-control runes, and displays at most 4096 runes. On RFB 3.3 and 3.7 failure it reads no reason bytes.
- After decoding, sanitizing, and truncating, WinARD checks the final displayed reason again against temporary character buffers for both credentials using ordinal matching. If redaction and surrounding server bytes have recomposed either complete credential, the entire remote reason is discarded and only the fixed generic rejection message remains.
- Once a nonzero `SecurityResult` has been read, malformed, oversized, or truncated RFB 3.8 reason data is wrapped in `ArdAuthenticationRejectedException`; the numeric result code is retained and the protocol exception is available only as a payload-free inner exception.
- Exceptions and probe output contain no credentials, DH material, plaintext, or raw byte dumps.

## Independent known-answer test

The fixed test vector in `ArdAuthenticatorTests` was calculated outside the .NET implementation using Python integer `pow`, `hashlib.md5`, and `cryptography` AES-ECB. The auditable generator is [`tools/verify_ard_known_answer.py`](../../tools/verify_ard_known_answer.py). It fixes the modulus, server public key, client private exponent, credentials, and the full initial 128-byte random plaintext pattern, then verifies the expected fixed-width client public key and ciphertext consumed by the .NET test. This independently checks the NUL terminators and the deterministic bytes remaining after each terminator.

Run it with:

```text
python -m pip install cryptography
python tools/verify_ard_known_answer.py
```

The SHA-256 of the wire response (`ciphertext || clientPublicKey`) is:

```text
b83e83d40a70f71e00d3ba48c93e12ca5172b4ab364040d293ca9a23de4a1fa5
```

## Security scope

ARD security type 30 is a legacy scheme: its unauthenticated DH exchange, MD5 derivation, and ECB encryption do not provide modern channel security. This implementation exists only for interoperability. Transport protection such as the planned SSH tunnel belongs to a later task and is not added here.

.NET `BigInteger` uses immutable managed internal storage. WinARD minimizes the scope of private/shared `BigInteger` values and clears every controllable source and output byte buffer, but it cannot guarantee immediate zeroing of the runtime's internal `BigInteger` backing storage. No additional cryptography package or custom big-integer implementation is introduced to disguise this limitation.

## Probe and real-Mac validation

`WinARD.ProtocolProbe` performs only TCP connection, RFB negotiation, and ARD authentication. A single 30-second operation timeout covers connection, banner/security negotiation, challenge/response, and `SecurityResult`; Ctrl+C remains distinguishable from timeout. It reads `WINARD_HOST`, optional `WINARD_PORT` (default 5900), and `WINARD_USERNAME`, with interactive prompts as fallback. The password is always read from an interactive console without echo and is never logged.

Run:

```text
dotnet run --project tools/WinARD.ProtocolProbe
```

Expected successful output ends with:

```text
Security: AppleRemoteDesktop (30)
Authentication: success
```

Automated fixture interoperability is covered by tests. Real-Mac acceptance remains pending until a reachable Mac and credentials are provided by the user; no host, username, password, or packet capture is stored in the repository.
