# MVS delivery evidence and decision

## Delivery status

**研究门：关闭。** MVS is outside the WinARD delivery boundary for this stage.

The available research does not provide sufficient interoperability evidence for an implementation. In particular, there is no verified evidence for:

- an official protocol identifier;
- complete payload boundaries;
- pixel semantics; or
- cross-packet and continuous-state behavior.

A short observed prefix, a symbol name, or the presence of a library is not evidence for a complete decoder. These gaps prevent a bounded, independently verifiable implementation.

## Product boundary

WinARD **does not implement, claim, or distribute MVS or an RDM DLL**. The application must not advertise MVS support, negotiate it as a supported encoding, ship a decoder for it, or bundle/load an RDM binary as part of this delivery.

Normative project decision: **不实现、不声明、不分发 MVS 或 RDM DLL**。

This decision is a delivery gate, not an inference that an undocumented wire format is safe or compatible. It does not turn partial observations into a specification.

## Reopening the gate

Any future enablement requires **a separately approved specification and real-device evidence** before implementation or distribution begins. That evidence must establish, at minimum, the protocol identifier, complete framing and payload boundaries, pixel interpretation, state lifetime across packets and updates, error behavior, and repeatable interoperability on a real Mac.

Approval requirement: **单独批准的规格和实机证据**。

Until that separate approval is recorded, MVS and RDM DLL work remains excluded from WinARD releases.
