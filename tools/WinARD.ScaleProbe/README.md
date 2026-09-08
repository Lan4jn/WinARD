# Dynamic scale capability probe

This development-only console compiles the production ARD client, encryption,
message scheduler and decoders from source. It is not part of the portable app.

```powershell
dotnet build tools/WinARD.ScaleProbe/WinARD.ScaleProbe.csproj -c Release -p:Platform=x64
$probe = '.\tools\WinARD.ScaleProbe\bin\x64\Release\net8.0-windows10.0.19041.0\WinARD.ScaleProbe.exe'
& $probe --self-test
& $probe '<existing database path>' --inspect-saved
& $probe '<existing database path>' --run-single
```

`--inspect-saved` reads only, emitting anonymous indices, backend categories,
SSH flags and count. `--run-single` refuses multiple saved devices; an explicit
device GUID may be supplied instead. Only direct connections with a Windows
Credential Manager password are supported. Other backends require the existing
application's interaction flow; no secrets are printed or exported.

The test creates a separate session, starts at 100% with RGB565 and Zlib-first,
then writes 50% and 100% requests at completed framebuffer boundaries. There is
no reconnect or compatibility fallback. No keyboard, mouse or clipboard output
is sent. Each stage observes for at least six seconds, with a 25-second deadline;
the entire operation is bounded by 100 seconds. Ctrl+C cancels and disposes the
test connection. The active GUI session is not stopped.

Only pixel-bearing framebuffer dimensions confirm observed sizes. Multiple
dimensions may appear after a change due to queued older frames. A timeout or
decoder failure is inconclusive about server capability. A complete successful
round trip needs both reduced pixel dimensions and restored original dimensions
within this single session. The probe does not reset the compression stream or
fabricate a local resize. The FPS/MiBps statistics use forced full update requests
to provoke pixels on a static screen; they are not normal incremental-session
performance measurements. No image captures are written.
