# Online scale investigation

## Observed Mac session

The saved DNS connection was tested using the production ARD authentication,
encryption and framebuffer decoder in `tools/WinARD.ScaleProbe`.
The same connection received a real 3360 x 2100 pixel frame, sent the current
50% scaling message, received 45 further pixel updates over approximately six
seconds, then sent 100%. All observed pixel sizes remained 3360 x 2100.
The connection completed normally. Full-update throughput is not a daily
incremental-frame-rate benchmark.

This establishes that the current message and sequence did not change the
received framebuffer size on this target. It does not establish that all
macOS server-side scaling mechanisms are unsupported.

## Reference checks

The existing writer sends ten bytes: type 8, zero padding, and a big-endian
binary64 fraction. Its introducing commit is `da41b9a`. Repository documents
and byte tests do not provide a traceable successful Mac capture for it.

LibVNC defines an unrelated UltraVNC type-8 extension as four bytes:
type, integer divisor, two padding bytes. Its server divides framebuffer
dimensions by the divisor. This is evidence of an extension collision,
not permission to substitute the UltraVNC message into an ARD session.

- https://github.com/LibVNC/libvncserver/blob/master/include/rfb/rfbproto.h
- https://github.com/LibVNC/libvncserver/blob/master/src/libvncclient/rfbclient.c
- https://github.com/LibVNC/libvncserver/blob/master/src/libvncserver/rfbserver.c

The public noVNC-ARD constants name type 8 `SetServerScaling`, but its ARD
patch does not import or send that message. It therefore corroborates the
message identifier, not the payload layout or a working scaling sequence.

- https://github.com/peetinc/noVNC-ARD/blob/master/ard/ard-constants.js
- https://github.com/peetinc/noVNC-ARD/blob/master/ard/ard-patch.js

## Remaining evidence and protocol status

**Protocol status conclusion: 证据不足 (Insufficient Protocol Evidence)**

1. A working ARD client trace or a traceable Apple-specific implementation is
needed to verify the payload layout, parameter encoding, byte order, display-selection,
and any update prerequisites.
2. Record actual received pixel dimensions, not local display zoom. Do not enable
automatic online scaling based on a successful write alone.
3. In accordance with the implementation plan and specification exit criteria,
blind speculative permutation of control bytes is rejected. Online automatic
scaling switching remains disabled until verified evidence is obtained. The product
maintains pre-connection settings with experimental markings and clear unconfirmed
state presentation.
