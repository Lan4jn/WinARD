# RFB initialization and core framebuffer encodings

After Apple Remote Desktop security type 30 authentication succeeds, WinARD performs the standard RFB initialization exchange over the same write-through stream. The protocol layer does not flush or dispose that stream.

## Initialization

WinARD sends `ClientInit` with the shared flag set to `1`. It then reads `ServerInit`:

- width and height are unsigned 16-bit big-endian values and must both be non-zero;
- the 16-byte pixel format is validated before use;
- the server-name byte length is bounded by `ProtocolLimits.MaxMessageBytes` before allocation or payload reads;
- the complete permitted name payload is consumed, decoded as UTF-8 with replacement, cleaned to a single line, and truncated to 4096 UTF-16 code units for display.

The accepted server pixel formats are true-color 8, 16, or 32 bits per pixel. Flags must be zero or one, component maxima must be non-zero masks of the form `2^n - 1`, and shifted component masks must fit within the pixel width without overlap.

WinARD then requests this canonical client format:

| Field | Value |
| --- | ---: |
| bits per pixel | 32 |
| depth | 24 |
| byte order | little-endian |
| true color | yes |
| red/green/blue maximum | 255 / 255 / 255 |
| red/green/blue shift | 16 / 8 / 0 |

The declared encodings, in preference order, are ZRLE (`16`), Raw (`0`), CopyRect (`1`), Cursor (`-239`), and DesktopSize (`-223`). ZRLE is preferred for compressed updates, with Raw retained as the fallback. Encoding IDs are signed 32-bit big-endian values. This declaration does not establish that the real-Mac capture described below selected or sent ZRLE.

The initial framebuffer request is non-incremental and covers the complete dimensions returned by `ServerInit`.

## Framebuffer representation

The in-memory framebuffer is a contiguous BGRA32 buffer. Every desktop pixel has alpha `255`; the high wire byte of a 32-bit source pixel is not treated as alpha. Allocation and resize sizes are calculated with checked wide arithmetic and rejected before allocation when they exceed `MaxFramebufferBytes`.

Pixel snapshots are defensive copies. Resize validates and allocates the replacement before changing dimensions or storage. Replaced and disposed buffers are zeroed on a best-effort basis, and operations after disposal fail.

For a true-color wire pixel, each component is extracted using its negotiated maximum and shift, then scaled with:

```text
component8 = (component * 255 + maximum / 2) / maximum
```

## FramebufferUpdate

`FramebufferUpdateReader.ApplyAsync` consumes a complete server-to-client message, including message type `0`. Rectangle counts above 4096 are rejected before the rectangle loop. Unknown encoding IDs are fatal because their payload length cannot be inferred safely.

Supported encodings:

- Raw: validates the destination and payload size, reads and converts into temporary buffers, then commits the rectangle. A failed Raw rectangle does not alter its destination.
- CopyRect: validates source and destination before copying. Overlap is handled with memmove-equivalent row ordering and overlapping row copies.
- DesktopSize: requires origin `(0,0)`, validates and allocates atomically, and reports the resized full framebuffer as dirty.
- Cursor: keeps cursor pixels separate from the desktop. Cursor mask bits are most-significant-bit first, with each mask row padded to a whole byte. A set bit makes the BGRA pixel opaque; a clear bit makes it transparent. A `0x0` cursor has no payload and represents an empty cursor.

Updates use rectangle-level commit semantics: rectangles completed before a later rectangle fails remain applied. The failing rectangle itself is not partially committed. Dirty rectangles preserve successful wire order.

Each `FramebufferUpdate` has two independent budgets. `MaxFramebufferUpdateBytes` limits bytes consumed from decoder payloads. `MaxFramebufferUpdateWorkBytes` limits estimated decoding work before reads, allocations, or copies: Raw charges wire bytes plus conversion and framebuffer writes, CopyRect charges copied BGRA bytes, DesktopSize charges replacement framebuffer initialization, and Cursor charges wire/mask bytes plus conversion and the defensive cursor copy. The budgets reset for every message. DesktopSize is additionally limited to four rectangles per update, and decoded cursor BGRA storage is bounded by `MaxCursorBytes`.

## Protocol probe capture

The probe still exits immediately after successful authentication by default. It only initializes the session and requests a framebuffer when the user supplies an explicit path:

```powershell
dotnet run --project tools/WinARD.ProtocolProbe -- --capture-first-frame artifacts\first-frame.bgra
```

The output format is selected by extension. `.bgra` writes exactly `width * height * 4` bytes in the framebuffer's top-down BGRA memory order with no header; `.bmp` remains available as an uncompressed 32-bit BMP. Missing parent directories are created and existing files are not overwritten. A unique temporary file is flushed and closed before a no-overwrite move publishes the final capture.

Capture succeeds only after Raw pixel-content rectangles cover the complete current framebuffer. Coverage uses one fixed bit per pixel, resets after DesktopSize, observes cancellation while scanning, and may combine rectangles across at most 64 updates. Incomplete updates cause another full non-incremental request. Cursor, CopyRect, and DesktopSize do not count as initial pixel coverage. Probe output shows at most eight dirty rectangles followed by an omitted count, and never prints the server name.

## Real-Mac interoperability evidence

On 2026-07-25, the user confirmed a successful first-frame capture against a real Mac with the following non-sensitive results:

- RFB version `3.8`;
- Apple Remote Desktop security type `30`;
- authentication succeeded;
- framebuffer dimensions `3360x2100`;
- the reported dirty rectangle covered the full framebuffer: `(0,0) 3360x2100`.

The generated BMP decoded successfully during visual inspection. Its orientation and color channels were correct, and the captured content covered the complete screen. The Mac host, username, password, capture path, and screenshot contents were not recorded. The specific macOS major version was not provided, so this result is not attributed to a particular macOS release.
