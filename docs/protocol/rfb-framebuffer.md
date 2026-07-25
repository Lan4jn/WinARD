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

The declared encodings, in preference order, are Raw (`0`), CopyRect (`1`), Cursor (`-239`), and DesktopSize (`-223`). Encoding IDs are signed 32-bit big-endian values. ZRLE is intentionally not declared by this implementation stage.

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

## Protocol probe capture

The probe still exits immediately after successful authentication by default. It only initializes the session and requests a framebuffer when the user supplies an explicit path:

```powershell
dotnet run --project tools/WinARD.ProtocolProbe -- --capture-first-frame artifacts\first-frame.bgra
```

The output format is selected by extension. `.bgra` writes exactly `width * height * 4` bytes in the framebuffer's top-down BGRA memory order with no header; `.bmp` remains available as an uncompressed 32-bit BMP. Missing parent directories are created and existing files are not overwritten. Capture succeeds only when the first update contains at least one dirty rectangle. The probe reports the dimensions, path, and dirty rectangles, and never prints the server name.
