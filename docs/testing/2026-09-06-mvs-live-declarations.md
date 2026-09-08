# RDM High / Adaptive live declaration observations

## High / Default

Observed through the loopback synthetic ARD server on 2026-09-06.
Authentication, ClientInit and ServerInit completed. The client sent ViewerInfo
(0x21), SetMode (0x0A), then SetEncodings (0x02):

```text
1002, 6, 0, -239, 1104, 1100, 1101, 1105, -223
```

The SetEncodings message was read completely. No first framebuffer request
was observed before the investigator stopped the connection to collect the
second setting. This is a partial client declaration, not a completed
initialization or real Mac codec selection. The probe's complete-report JSON
was not generated; this list comes from its bounded message-stage output.

No ViewerInfo payload, authentication material, remote address or screen
pixels are included here.

## Adaptive / Default

**Invalidated:** the operator subsequently confirmed that Performance had not
been changed. The observation below is not an Adaptive sample and must not be
used for a High/Adaptive comparison. A corrected capture is required.

On 2026-09-07 the operator reported connecting the Adaptive / Default temporary
entry to the same synthetic server. Authentication and initialization reached
the same stages. ViewerInfo (0x21), SetMode (0x0A), and a complete SetEncodings
(0x02) were observed. Encoding IDs, in order:

```text
1002, 6, 0, -239, 1104, 1100, 1101, 1105, -223
```

No first framebuffer request or later declaration was observed before the
investigator stopped the waiting connection. The two observed initial encoding
lists are identical. This does not establish identical complete negotiation:
the synthetic server sends no display metadata or subsequent framebuffer
response, and both observations stop at that boundary. It also does not identify
1002 as MVS. The selected UI setting is operator-reported, not independently
verified through an exported RDM configuration.

Next investigation: trace the initialization messages and server responses
required after SetEncodings, including display metadata. Establish the required
response from protocol evidence before extending the synthetic server. Repeating
the same capture without changing that boundary adds no new evidence.

## Corrected Adaptive / Default observation

On 2026-09-07 the operator changed Performance to Adaptive and reconnected.
The probe completed authentication, ClientInit, ServerInit, ViewerInfo and
SetMode, then read the complete SetEncodings message:

```text
1011, 6, 0, -239, 1104, 1100, 1101, 1105, -223
```

Compared with the valid High observation, the first entry changes from 1002 to
1011; all remaining encoding IDs retain the same order. Combined with the
official Adaptive-to-MVS option mapping, this identifies 1011 as an observed
MVS negotiation candidate. It does not establish its payload format or prove
that a real Mac selected it. Neither capture reached the first framebuffer
request; differences in unrecorded control payloads or later exchanges have not
been ruled out. The earlier unmodified-Performance sample remains invalid as
Adaptive evidence.

## Real Mac selection: 2026-09-07

The isolated ScaleProbe reused production authentication, encryption and a
confirmed RGB565 standard framebuffer session, then sent the observed Adaptive
encoding list. No scaling request or keyboard/pointer event was sent.

```text
Initialized=9376x3384
Stage=baseline-100 PixelSize=9376x3384
Stage=baseline-100 FullPixelFrames=53 Seconds=6.105 FPS=8.68 MiBps=1.001
MvsSelection=HeaderObserved EncodingId=1011 DecoderImplemented=False
```

The production reader reported UnsupportedEncoding at FramebufferRectangleHeader
with EncodingId 1011. The probe recognized only that exact category and closed
the connection. No unknown payload was interpreted or saved (the encrypted
transport can internally buffer a complete packet). This establishes actual
server selection after online SetEncodings, not complete MVS decoding or frame
validity. Baseline throughput is forced-update probe data, not MVS throughput.
The current desktop layout differs from the prior 3360 x 2100 scale experiment.

## Real Mac 1011 bounded sample capture: 2026-09-07

With user confirmation and synthetic test pattern displayed, `ScaleProbe` connected to the real Mac and initiated 1011 sample capture with preserved zlib inflater state for transition frames:

```text
Initialized=9376x3384
Stage=baseline-100 PixelSize=9376x3384
Stage=baseline-100 FullPixelFrames=63 Seconds=6.075 FPS=10.37 MiBps=1.193
MvsCandidateObserved=True Rectangle=0x0 at (0,0)
MvsSetupCaptured=True PrefixLength=133 SHA256=C02CECE7B757BC6AF2C5CA3A74A4C0D798B375A27EEC3CC2DB9BD9FAF3B7FBAD
MvsCandidateObserved=True Rectangle=6016x3384 at (3360,0)
MvsSampleCaptured=True SampleName=solid-blue Rectangle=6016x3384 PrefixLength=65536 SHA256=98E95F38EB7ACFA7725C6ACBA0D1BE14689A47FB9ABB464A56DC5D2AE1A76D18
```

### Key Payload Structure Findings
1. **Setup Frame (Pseudo-Encoding Metadata)**:
   - Coordinates: `Rectangle 0x0 at (0,0)`.
   - Length Header: 4-byte big-endian uint32 (`00 00 00 81` = 129 bytes payload, total 133 bytes).
   - Payload Decomposition:
     - 1 byte flag/mode (`0x02`, indicating 2 tables);
     - 64 bytes Luminance Quantization Table (`0C 09 09 0B 12 11 12 19 ...`);
     - 64 bytes Chrominance Quantization Table (`0F 0F 12 24 ... 4B 4B 4B ...`).
   - SHA256: `C02CECE7B757BC6AF2C5CA3A74A4C0D798B375A27EEC3CC2DB9BD9FAF3B7FBAD`.
2. **Image Slice Frame**:
   - Coordinates: `Rectangle 6016x3384 at (3360,0)`.
   - Length Header: 4-byte big-endian uint32 (`00 05 90 EF` = 364,783 bytes payload).
   - Bounded Capture: 65,536 bytes prefix safely recorded under `artifacts/protocol-research/samples/solid-blue/`.
   - SHA256: `98E95F38EB7ACFA7725C6ACBA0D1BE14689A47FB9ABB464A56DC5D2AE1A76D18`.
3. **Protocol Conclusion**:
   - 1011 frames strictly use 4-byte big-endian length prefix.
   - 1011 session starts with a 0x0 metadata setup frame defining custom JPEG/DCT quantization tables before delivering image slices.
