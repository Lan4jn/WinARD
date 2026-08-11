# ARD zero-install image-quality baseline

This note defines the image-quality capabilities deliverable in the current stage. The baseline uses protocol behavior that WinARD can implement and validate independently, while requiring no software installation on the Mac.

## Deliverable standard capabilities

WinARD may use the following standard RFB framebuffer encodings:

| Capability | Wire encoding ID | Delivery role |
| --- | ---: | --- |
| RFB Zlib encoding 6 | `6` | Standard compressed rectangle data |
| ZRLE 16 | `16` | Standard compressed tiled rectangle data |
| Raw 0 | `0` | Standard uncompressed fallback |

All three operate with a negotiated **standard PixelFormat**. Pixel depth, byte order, true-color maxima, and component shifts come from the RFB pixel-format exchange rather than from an Apple-private pixel interpretation. A decoder is deliverable only when its framing, persistent-state rules, bounds, and pixel conversion are implemented and tested.

## ARD scaling control

The deliverable scaling control is the ARD wire sequence **`08 00 + IEEE 754 binary64 big-endian`**: bytes `08 00` followed by the requested scale value encoded as an eight-byte IEEE754 binary64 value in network (big-endian) byte order. The accepted range is **`0 < factor <= 1`**; non-finite values and values outside that range are rejected before any bytes are written.

The writer implements this wire shape, and automated tests prove deterministic byte order and rejection of malicious boundary inputs. This note does not link traceable real-device interoperability evidence, so `ServerScaling` capability remains `Unknown` and scaling remains disabled until such evidence is reviewed and linked.

This control changes the requested remote image scale; it does not create evidence for any private framebuffer decoder.

## Apple-private encoding gates

**Apple 1002/1001** encoding IDs pass only through an independent evidence gate. Before either ID can be enabled, a separately reviewed specification and real-Mac capture must establish the full rectangle framing, payload boundaries, pixel semantics, state continuity, bounds, and failure behavior. **Prefix evidence is not decoder evidence**: recognizing an ID or a leading byte sequence does not justify negotiating, decoding, or advertising the encoding.

**Apple 1000** is a black-and-white encoding and does not enter automatic mode. WinARD must not automatically select it as an image-quality or bandwidth adaptation step.

MVS is governed by the separate closed delivery gate in [MVS delivery evidence and decision](ard-mvs-evidence.md) and is not part of this baseline.

## Deployment boundary

The Mac side is **zero-install**. Operationally, this is **Mac 端零安装**. WinARD relies on the remote services and protocol capabilities already provided by macOS; this stage does not deploy agents, codecs, extensions, or helper binaries to the Mac.
