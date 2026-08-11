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

### Direct prefix research boundary

The research probe accepts only `--capture-known-encoding-prefix {1002|1001} <output-dir> --confirm-synthetic-screen`. It declares encodings in the exact order `[candidate, 6, 16, 0]`. A response using encoding 6, 16, or 0 is recorded as `candidate-not-observed` with the observed encoding ID; it is never counted as candidate success.

For each candidate, research requires separate `solid-color`, `gradient`, `text`, and `multiple-rectangle-sizes` sample directories. The multi-size directory contains three independently confirmed variants. Before each connection the interactive probe names the required synthetic scene or size variant and requires the operator to type that exact name; a redirected/non-interactive run stops before connecting. Each variant then uses a fresh connection because a bounded prefix does not establish where the unknown payload ends; bytes from one variant are never reused as the next variant's framing. Each directory contains only a bounded payload prefix, its declared payload length, observed prefix length, SHA-256 hash and safe rectangle metadata. The set manifest records each variant's actual wire rectangle. Publication fails unless the three multi-size variants contain at least two distinct wire `Width × Height` pairs; a repeated same-size rectangle cannot satisfy the evidence requirement. These files live under the Git-ignored `artifacts/protocol-research` tree, outside ordinary diagnostics. Manifests contain no host, username, password, or absolute path. The complete set is built in a private staging root; only a complete set with a `capture-set.json` variant/rectangle/hash index is atomically published. Any failure removes the staging root and leaves the requested output path absent.

On Windows the staging root uses a protected ACL owned by the current user, inherited by its children. Before publication the probe repeats reparse-point checks, reopens every sample manifest and bounded prefix, verifies declared/observed lengths and SHA-256, verifies the set-manifest variant/rectangle/hash mapping, and checks the parent, target, and staging paths again immediately before the atomic rename. This is a fail-closed mitigation for path replacement within managed .NET APIs; it is not a claim that a path-name check eliminates every race against a more privileged local process.

The prefix reader records only an outer rectangle header, the untrusted declared length, and an observed bounded prefix. It does not prove that the declared length is a complete payload boundary or establish pixel decoding.

- **Apple 1002: 关闭（未验证）.** There is no evidence for complete payload boundaries, pixel semantics, continuous-state behavior, malicious-input handling, or macOS 26.5 interoperability.
- **Apple 1001: 关闭（未验证）.** There is no evidence for complete payload boundaries, pixel semantics, continuous-state behavior, malicious-input handling, or macOS 26.5 interoperability.

No real-device hard-gate capture was executed for this decision. In this task the user has not visually confirmed a single-display synthetic screen with no sensitive content and notifications/private filenames disabled. Until that on-site confirmation and the full evidence set exist, both gates remain closed; no decoder is implemented or enabled.

MVS is governed by the separate closed delivery gate in [MVS delivery evidence and decision](ard-mvs-evidence.md) and is not part of this baseline.

## Deployment boundary

The Mac side is **zero-install**. Operationally, this is **Mac 端零安装**. WinARD relies on the remote services and protocol capabilities already provided by macOS; this stage does not deploy agents, codecs, extensions, or helper binaries to the Mac.
