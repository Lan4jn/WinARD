# macOS zero-install adaptive-quality acceptance runbook

## Current validation status

This runbook targets a real **macOS 26.5** host with **one display**. The real-host matrix, WinUI manual acceptance, and diagnostic-export acceptance have **not been executed and are unverified**. Automated unit, build, packaging, archive, and process-lifetime checks are reported separately from this manual runbook and do not establish real-host behavior.

No row may be changed to a successful result without evidence from the exact candidate build and the fixed environment below.

## Percentage-scale and reconnect gate

The Mac remains zero-install: use only macOS Remote Management (and Remote Login for SSH). For the exact candidate, connect separately with Automatic, 100%, 75%, 50%, and 25%. A percentage is resolved before connection and cannot change inside an established byte stream. If changed during a session, verify the reconnect-required notice and both “reconnect now” and “later” paths.

For every scale, record only the original framebuffer size, the sanitized applied framebuffer width/height, the stable bootstrap attempt/reason, aggregate throughput/FPS/response, and whether fallback occurred. An incompatible preferred bootstrap may create exactly one fresh `100% + BGRA32 + Zlib-first` fallback connection. Also interrupt a connected session, verify reconnect attempt/countdown/cancel, and verify deterministic authentication/protocol/host-key failures do not retry.

All percentage-scale, fallback, automatic-reconnect, full-screen shortcut, settings, and credential-migration real-host results remain **Not run / External gate** until this section is executed. Automated tests do not establish a bandwidth reduction claim.

## Privacy and evidence boundary

Record only aggregate telemetry and enumerated product state:

- actual encoding name;
- average and peak throughput in MiB/s;
- actual FPS;
- response in milliseconds;
- input write latency in milliseconds;
- content state;
- Q-level transitions;
- whether the configured target was satisfied;
- concise, content-free observations and evidence references.

Do **not** capture or record the remote picture, screenshots, video, entered or displayed content, pointer coordinates, key names, keystrokes, keyboard text, clipboard text, host names, addresses, credentials, or secrets. Evidence references must identify sanitized diagnostic artifacts only. A diagnostic archive must be reviewed for these exclusions before it leaves the test machine.

## Fixed environment

Use the same values for every matrix row:

| Attribute | Required value | Current status |
|---|---|---|
| Remote operating system | macOS 26.5 | 未验证 |
| Display topology | One display | 未验证 |
| Resolution | One unchanged resolution for the entire matrix | 未验证 |
| Network | One unchanged network path and traffic-shaping setup | 未验证 |
| Candidate | The same Release x64 portable build for every row | 未验证 |
| Office workload | The content-free script below, with unchanged phase timing | 未验证 |
| Competing workload | No unrelated foreground or network workload | 未验证 |

If any fixed attribute changes, discard the affected measurements and rerun the entire affected comparison set. Do not merge results from different candidates or environments.

## Repeatable office workload

The immutable workload identifier is **ZAQ-OFFICE-1.0**. Its phase order, durations, action categories, counts, and cadence are fixed below. Any workload change requires a new identifier; results produced under different identifiers must not be compared as the same workload.

Before every repetition, reconnect cleanly, restore the approved office application to the controlled starting state, and allow telemetry to settle for 60 seconds. Then execute these phases in order with a monotonic timer:

1. **Idle — 60 seconds:** perform zero input actions.
2. **Pointer navigation — 60 seconds:** perform exactly 30 approved pointer-navigation actions, starting one action every 2 seconds.
3. **Scroll bursts — 60 seconds:** perform exactly 12 one-second scroll bursts, starting one burst every 5 seconds; each burst uses the same direction pattern and rate defined by ZAQ-OFFICE-1.0.
4. **Window drag and resize — 60 seconds:** alternate exactly 6 drag actions and 6 resize actions. Start one action every 5 seconds, spend 2 seconds on the action, then remain inactive for 3 seconds.
5. **Text-entry category — 60 seconds:** perform exactly 6 five-second entry bursts at 4 character events per second, followed by 5 seconds without input after each burst. Use the approved non-sensitive corpus associated with ZAQ-OFFICE-1.0.
6. **Recovery — 180 seconds:** stop all activity and perform zero input actions while observing staged recovery.
7. **Close:** end the session cleanly and export the sanitized diagnostics for metric extraction.

Run every matrix row three times. Compute the reported average and peak from the same phase intervals on every repetition, then record the median repetition. The run record stores only the SOP identifier **ZAQ-OFFICE-1.0**, repetition number, aggregate metrics, enum-like states, and sanitized evidence reference. It never stores the office corpus, action content, coordinate, key name, or action-level trace. A repetition interrupted by operator error is excluded with a content-free reason and repeated from the controlled starting state.

## Metric rules

- **Actual encoding:** the encoding reported by sanitized runtime telemetry; do not infer it from appearance.
- **Average / peak MiB/s:** received image-payload rate over the product's measurement windows, reported separately as average and maximum.
- **Actual FPS:** delivered framebuffer update rate over the same interval used for throughput.
- **Response:** product-reported end-to-end response estimate in milliseconds.
- **Input write latency:** product-reported time from local enqueue to completed transport write in milliseconds.
- **Content state:** only the enum-like state, such as Idle, Interactive, Motion, or Recovery.
- **Q-level transitions:** ordered Q values with sanitized timestamps or elapsed durations; record every change and its reported reason.
- **Target satisfied:** use the product-reported result and corroborate it against the configured bandwidth and refresh targets; values are Yes, No, or 未验证.
- **Observation / evidence:** concise status plus a reference to sanitized telemetry. It must not contain remote or input data.

The configured bandwidth is a sustained objective rather than a per-packet ceiling. Short bursts are recorded through the peak metric and do not alone establish failure.

## Matrix

The matrix is the Cartesian product of five profiles, four bandwidth targets, and six refresh targets. Bandwidth values are MiB/s; numeric refresh values are FPS. Selecting an override may cause the UI to present the effective configuration as Custom; keep the **starting profile** in the Profile column and record the actual effective encoding and Q behavior from telemetry.

| ID | Profile | Bandwidth MiB/s | Refresh target | Actual encoding | Avg MiB/s | Peak MiB/s | Actual FPS | Response ms | Input write latency ms | Content state | Q-level transitions | Target satisfied | Observation / evidence |
|---:|---|---:|---|---|---:|---:|---:|---:|---:|---|---|---|---|
| 001 | Automatic | 1 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 002 | Automatic | 1 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 003 | Automatic | 1 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 004 | Automatic | 1 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 005 | Automatic | 1 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 006 | Automatic | 1 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 007 | Automatic | 2 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 008 | Automatic | 2 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 009 | Automatic | 2 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 010 | Automatic | 2 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 011 | Automatic | 2 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 012 | Automatic | 2 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 013 | Automatic | 4 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 014 | Automatic | 4 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 015 | Automatic | 4 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 016 | Automatic | 4 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 017 | Automatic | 4 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 018 | Automatic | 4 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 019 | Automatic | 8 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 020 | Automatic | 8 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 021 | Automatic | 8 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 022 | Automatic | 8 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 023 | Automatic | 8 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 024 | Automatic | 8 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 025 | Original | 1 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 026 | Original | 1 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 027 | Original | 1 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 028 | Original | 1 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 029 | Original | 1 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 030 | Original | 1 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 031 | Original | 2 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 032 | Original | 2 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 033 | Original | 2 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 034 | Original | 2 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 035 | Original | 2 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 036 | Original | 2 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 037 | Original | 4 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 038 | Original | 4 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 039 | Original | 4 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 040 | Original | 4 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 041 | Original | 4 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 042 | Original | 4 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 043 | Original | 8 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 044 | Original | 8 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 045 | Original | 8 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 046 | Original | 8 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 047 | Original | 8 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 048 | Original | 8 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 049 | Balanced | 1 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 050 | Balanced | 1 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 051 | Balanced | 1 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 052 | Balanced | 1 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 053 | Balanced | 1 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 054 | Balanced | 1 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 055 | Balanced | 2 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 056 | Balanced | 2 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 057 | Balanced | 2 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 058 | Balanced | 2 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 059 | Balanced | 2 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 060 | Balanced | 2 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 061 | Balanced | 4 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 062 | Balanced | 4 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 063 | Balanced | 4 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 064 | Balanced | 4 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 065 | Balanced | 4 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 066 | Balanced | 4 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 067 | Balanced | 8 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 068 | Balanced | 8 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 069 | Balanced | 8 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 070 | Balanced | 8 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 071 | Balanced | 8 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 072 | Balanced | 8 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 073 | Smooth | 1 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 074 | Smooth | 1 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 075 | Smooth | 1 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 076 | Smooth | 1 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 077 | Smooth | 1 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 078 | Smooth | 1 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 079 | Smooth | 2 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 080 | Smooth | 2 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 081 | Smooth | 2 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 082 | Smooth | 2 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 083 | Smooth | 2 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 084 | Smooth | 2 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 085 | Smooth | 4 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 086 | Smooth | 4 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 087 | Smooth | 4 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 088 | Smooth | 4 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 089 | Smooth | 4 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 090 | Smooth | 4 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 091 | Smooth | 8 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 092 | Smooth | 8 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 093 | Smooth | 8 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 094 | Smooth | 8 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 095 | Smooth | 8 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 096 | Smooth | 8 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 097 | Custom | 1 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 098 | Custom | 1 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 099 | Custom | 1 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 100 | Custom | 1 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 101 | Custom | 1 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 102 | Custom | 1 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 103 | Custom | 2 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 104 | Custom | 2 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 105 | Custom | 2 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 106 | Custom | 2 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 107 | Custom | 2 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 108 | Custom | 2 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 109 | Custom | 4 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 110 | Custom | 4 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 111 | Custom | 4 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 112 | Custom | 4 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 113 | Custom | 4 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 114 | Custom | 4 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 115 | Custom | 8 | Automatic | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 116 | Custom | 8 | 30 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 117 | Custom | 8 | 45 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 118 | Custom | 8 | 60 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 119 | Custom | 8 | 120 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |
| 120 | Custom | 8 | Unlimited | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未执行 | 未验证 | 未执行 / 未验证 |

## Motion and recovery acceptance record

| Objective | Current result |
|---|---|
| Motion does not remain at 3–4 FPS for a sustained interval | 未执行 / 未验证 |
| Actual FPS approaches 60 when network and server capacity allow | 未执行 / 未验证 |
| Constrained operation stabilizes at 45 or 30 FPS when appropriate | 未执行 / 未验证 |
| Automatic adaptation never enters black-and-white output | 未执行 / 未验证 |
| After motion stops, quality recovers progressively rather than jumping unpredictably | 未执行 / 未验证 |
| The input queue remains bounded under sustained interaction | 未执行 / 未验证 |
| Every automatic change has a telemetry reason consistent with the observation | 未执行 / 未验证 |

A profile is accepted only when all its applicable matrix rows have sanitized evidence and the objectives above are verified. Any unexplained transition, unbounded queue growth, persistent 3–4 FPS motion, black-and-white selection, or missing measurement is a failed manual gate.

## WinUI and release-candidate manual checks

An automated synthetic process-lifetime smoke check was executed against the packaged `WinARD.Desktop.exe` with `--remote-session-smoke`: the process remained alive for five seconds and was then terminated automatically. This establishes only that the packaged process reached a sustained smoke lifetime; it does not verify rendering, interaction, connection, diagnostic export, graceful shutdown, or any WinUI manual result below.

| Check | Current result |
|---|---|
| Profile, bandwidth, and refresh controls expose all matrix values | 未执行 / 未验证 |
| Runtime status exposes actual encoding, throughput, FPS, response, content state, Q level, and target status | 未执行 / 未验证 |
| Quality transitions remain understandable through accessible status text | 未执行 / 未验证 |
| Portable executable starts in an interactive Windows desktop session | 未执行 / 未验证 |
| Portable executable connects to the fixed macOS 26.5 host | 未执行 / 未验证 |
| Diagnostic export completes through WinUI | 未执行 / 未验证 |
| Exported diagnostics contain the required aggregate fields and no prohibited data | 未执行 / 未验证 |

## Completion rule

Automated tests and archive verification may establish code and package integrity, but cannot change any real-host or WinUI row above. Release acceptance requires an authorized operator to execute the full fixed matrix and the manual checks on the exact candidate, preserve only sanitized aggregate evidence, and leave no row unverified.
