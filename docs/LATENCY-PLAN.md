# Latency plan: exclusive-mode toggle and the real floor

**Status (v0.6.0):** Phases A, B, C0 and C are implemented. L1 measured cadence (`CadenceTracker`, raised live by
the health tick), L2 event-driven loopback with keep-alive (`WasapiCaptureClient.CaptureMode.EventLoopback`),
L3 AUTO decay memory, L4 per-input ring targets (`RingBuffer.FillTargetFrames`, fill-surplus barrier), and the
L5 ⚡ exclusive toggle (`AppConfig.ExclusiveMode`, rungs R0/C0 in the WASAPI clients, all-or-nothing across
outputs). Phase D (raw mode, MMCSS critical, mixed exclusive/shared outputs) remains open.

Measured on the development machine after implementation (Intel mic + VB-Cable loopback → Realtek, AUTO,
14 s runs, app process posture):

| Configuration | Effective total | Mic route | Loopback route | Underruns after start-up |
|---|---|---|---|---|
| Shared (before) | 54 advertised, 64 to 84 real | — | — | storms every few minutes |
| Shared (now) | 58 | 44 to 48 | 66 | 0 |
| Exclusive, mic only | 18 | 15 to 19 | — | 0 |
| Exclusive, mic + loopback | 46 | 16 to 19 | 44 to 55 | 0 |

A root cause found during implementation, fixed in the provider and worth recording: the "skip late data to
stay on the timeline" realign rule was self-sustaining. Once a live source fell one render gulp behind, every
arriving frame was skipped and every block added a fresh shortfall, so the ring sat at zero with one underrun
per block until something (a headroom hold) handed the producer a head start. That is the 54 → 84 → 54 cycle
in the field log. A starved live source now re-anchors its ring to target for every consumer at once
(`MixingSampleProvider.StarvationReanchors`).

Companion to `FIX-PLAN.md`. Scope: lower end-to-end latency as far as the hardware allows, with every change
either globally safe (on by default) or behind a toggle that is off by default. All numbers below were measured
on the development machine on 2026-10-13 (Windows 11, NVIDIA HDMI sinks, Realtek, VB-Cable, Intel mic array)
with a throwaway probe compiled against the engine's own WASAPI interop; nothing is assumed from documentation.

## 1. What the hardware offers

| Endpoint | Shared-mode period (IAudioClient3 min / default) | Exclusive minimum period | Exclusive formats accepted |
|---|---|---|---|
| NVIDIA HDMI Left / Right, Mi TV (render) | 10 ms / 10 ms | 3 ms | pcm16, pcm24-in-32 |
| Realtek speakers (render) | 10 / 10 | 3 ms | pcm16, pcm24, pcm24-in-32 |
| FxSound (render, APO endpoint) | 10 / 10 | 3 ms | pcm24 |
| VB-Cable (render) | 2.67 / 10 | 2 ms | pcm16, pcm24 |
| VB-Cable (capture = "Steam Streaming Microphone") | 10 / 10 | 3 ms | pcm16, pcm24 |
| Intel mic array (capture) | 2 / 10 | 2 ms | pcm16 |

Shared mode is pinned at 10 ms on every physical sink in this setup. Exclusive mode offers 3 ms on all of them.

## 2. How audio actually arrives (delivery-gap distributions, 6 s each)

| Source | Mode | gap p50 | gap p99 | gap max | Note |
|---|---|---|---|---|---|
| VB-Cable loopback | polled, 10 ms (current engine) | 15.5 | 30.4 | 42 | poll race with the 10 ms packet clock |
| VB-Cable loopback | event-driven + keep-alive | 15.1 | 16.7 | 17.1 | 1 to 2 packets per burst |
| VB-Cable capture (mic side) | event-driven, shared 10 ms | 15.1 | 16.7 | 17.0 | same burst pattern as its loopback |
| Realtek loopback | polled, 10 ms | 15.5 | 30.4 | 31 | poll race |
| Realtek loopback | event-driven + keep-alive | 10.0 | 10.2 | 10.4 | clean |
| Intel mic | event-driven, shared 2 ms | 2.0 | 2.2 | 2.5 | clean |

Two conclusions:

1. **VB-Cable delivers in ~16 ms bursts**, on both its capture side and its loopback, whatever WASAPI period is
   requested. That is the virtual cable's own internal engine, not Windows. The engine currently books its cadence
   as 10 ms.
2. **Polling a loopback at its packet period races the packet clock.** The tail reaches 30 to 42 ms. Event-driven
   loopback with a silent keep-alive render stream on the same endpoint removes the tail (max 17 ms on VB-Cable,
   10.4 ms on Realtek). The engine's note that event-driven loopback "doesn't signal reliably" describes the
   no-keep-alive case; with the keep-alive it delivered 601 of 601 expected packets in every run.

## 3. Where the 64 ms goes, and why it thrashes

From the probe log at AUTO:

```
capture cadence 10  +  ring 32 (padding 12 + cadence 10 + auto headroom 10)  +  render 22 (padding 12 + period 10)  = 64
```

- The two "10 ms cadence" terms are wrong for VB-Cable inputs: the real worst gap is 17 ms event-driven and 30 to
  42 ms polled. The ring floor (22 ms at headroom 0) therefore cannot hold, and the log shows exactly that: every
  decay to headroom 0 is followed within minutes by a storm (240, 2127, 2997, 2759 underruns per 10 s), the loop
  climbs to +30 or +40 and the safety tier, then decays after five clean minutes and the cycle repeats. The true
  operating point is 64 to 84 ms, not the advertised 54.
- Everything else in the budget is honest. The render terms are what shared mode costs on a 10 ms engine; the
  only way below them is a smaller period, which these sinks grant only in exclusive mode.
- Not in the budget at all, and not controllable: the sink's own hardware latency (the TV's audio processing on
  HDMI can be tens of ms; "game mode" on the TV is the lever there) and VB-Cable's internal buffer (its control
  panel's "Max Latency" setting governs the 16 ms burst cadence; smaller values should shorten it, untested).

## 4. Levers, safest first

| # | Lever | Default | Effect on this setup | Risk |
|---|---|---|---|---|
| L1 | **Measured cadence.** Each capture client tracks the max inter-delivery gap over a rolling 3 s window after start; the budget uses that instead of the nominal period. | on | floor becomes honest: ≈ 61 ms with VB inputs, ≈ 44 with hardware inputs; storms stop; the 84 ms excursions disappear | none: it only ever raises a floor that was too low |
| L2 | **Event-driven loopback** with a silent keep-alive render stream on the endpoint; polled mode stays as the fallback rung. | on | loopback tail 42 → 17 ms (VB), 31 → 10 ms (Realtek); with L1 the loopback floor drops accordingly | low: keep-alive is one more silent stream per loopback endpoint; fall back to polled on any init failure |
| L3 | **AUTO decay memory.** The headroom loop never decays below the level that last stormed in this session (reset on restart or device change). | on | no five-minute thrash cycle; latency stays at the learned floor | none |
| L4 | **Per-input ring targets.** Each input's ring holds padding + its own cadence + headroom; the barrier releases when every ring is at its own target (fill-surplus semantics, reference implementation on branch `webview-legacy/buf-margin-hp-mode`). | on | a clean 2 ms mic is no longer taxed by a 17 ms loopback sharing the engine: mic routes ≈ 24 ms shared, loopback routes unchanged | medium: touches barrier and servo targets; needs the coordinator tests |
| L5 | **Exclusive mode toggle** (section 5). | **off** | render 22 → 6 ms, gulp 12 → 3 ms, hardware/VB mic cadence 10 → 3 ms | medium: takes endpoints from other apps; format negotiation; driver quirks |
| L6 | Raw-mode streams (`AUDCLNT_STREAMOPTIONS_RAW`, skips the endpoint's APO chain) | measure first | likely 0 ms here (no APOs on the HDMI sinks); can matter on Realtek with enhancements on | low, but bypasses the user's enhancement settings |
| L7 | MMCSS priority `AVRT_PRIORITY_CRITICAL` instead of the default for the audio threads | measure first | may shave scheduling jitter under load; no change to the budget | low |

Not levers: faster polling (the race remains, see table), sub-10 ms shared periods on the NVIDIA and Realtek
sinks (refused by the drivers), and porting the side branch's transport (same IAudioClient3 technique upstream
already uses; only its loopback and per-input-target pieces are worth taking, as L2 and L4).

### Expected totals on this setup

| Route | Today (real) | After L1 to L4 | After L5 (exclusive) |
|---|---|---|---|
| VB-Cable loopback → HDMI | 64 to 84 | ≈ 61, stable | ≈ 43 (17 + (3 + 17) + 6) |
| VB-Cable mic → HDMI | 64 to 84 | ≈ 61, stable | ≈ 43, or lower if exclusive capture breaks the burst pattern (unverified) |
| Realtek loopback → HDMI | 64 to 84 | ≈ 44 | ≈ 26 |
| Intel mic → HDMI | 64 to 84 | ≈ 26 (with L4) | ≈ 11 (2 + (3 + 2) + 6) |

## 5. Exclusive-mode toggle, specification

**Setting.** `AppConfig.ExclusiveMode` (bool, default false, additive: no config-version bump). `AudioEngine`
gets `ExclusiveMode` + `SetExclusiveMode(bool)`, applied at the next start; toggling while running restarts
through the normal fault/restart path (`RequestRestart`). `AppController.SetExclusiveMode` persists it.
`UiSnapshot.ExclusiveMode` and `MetricsSnapshot.ExclusiveActive` (true only when the running session actually
opened exclusive streams) plus a `ExclusiveBlockedReason` string for the tooltip.

**UI.** Corner key at row 1, column 3 (free slot above the view toggle), glyph ⚡, `active` class when the
session runs exclusive, disabled while locked. Tooltip: ON → "Exclusive mode: outputs run at 3 ms periods;
other apps cannot play to them while routing"; OFF → "Shared mode (10 ms periods). Click for exclusive"; blocked
→ the reason ("'Mi TV' is a loopback source", "refused by Windows: allow exclusive control in Sound settings",
"device in use by another exclusive app"). Per-device tier shows `Exclusive` in the metrics tier name.

**Eligibility, evaluated at every start.**
- Render endpoints that are not the source of any active loopback input (loopback taps the shared engine; an
  exclusive stream bypasses it and the tap goes silent).
- Capture endpoints that are not loopbacks.
- All-or-nothing per start for outputs: exclusive is used only if every routed output accepts it. Rationale: the
  sync coordinator assumes equal FIFO depth on every output; a 3 ms exclusive FIFO next to a 12 ms shared FIFO
  would play 9 ms early. Mixed sessions can be a later phase (compensate via the per-device delay line).
- Capture endpoints are independent: an eligible mic goes exclusive even when outputs stay shared.

**Render rung R0 (above the existing R1 to R4 ladder), only when the toggle is on and the device is eligible.**
1. Activate a fresh `IAudioClient`; `GetDevicePeriod` → minimum period; tier `Exclusive` at that period
   (degrade rung: 2 × minimum before falling to the shared ladder).
2. Format: try, in order, pcm24-in-32, pcm24, pcm16 at the mix rate and channel count, exact
   `IsFormatSupported(EXCLUSIVE)` only (closest-match answers are ignored). Build `WAVEFORMATEXTENSIBLE`.
3. `Initialize(EXCLUSIVE, EVENTCALLBACK, duration = period, periodicity = period, fmt)`.
   - `AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED` (0x88890019): read `GetBufferSize`, recompute the duration from the
     aligned frame count, re-activate, retry once.
   - `AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED` (0x8889000E), `AUDCLNT_E_DEVICE_IN_USE` (0x8889000A),
     `AUDCLNT_E_UNSUPPORTED_FORMAT`: record the reason, fall through to R1 (shared) on a fresh client.
4. Loop: exclusive event-driven streams are double-buffered by the driver with buffer = one period; every event
   writes the whole buffer. `FifoMs` reports one period; `ActualBufferMs` one period.
5. Converter: the existing `SampleConverter` PCM paths (16 / 24 / 32).

**Capture rung C0.** Same negotiation; cadence = period; buffer = period, so a late capture thread drops a packet
(`DATA_DISCONTINUITY`), which the existing adaptive degrade already counts; two demotions pin the device shared.

**Budget changes.** `LatencyBudget` learns a per-session `ExclusiveRender` flag: `renderPaddingMs = period`
(no +2 margin, nothing to top up), `renderLatencyMs = 2 × period`, `NeedsReallocation` never set for exclusive
outputs, `renderBufferRequestMs` ignored. Everything else (ring floor, knob, headroom loop) is unchanged and keeps
working because it is expressed in the same terms.

**Volume.** Exclusive streams bypass the endpoint volume and the Windows mixer: the matrix gain is the only level.
Say so in the tooltip and the README.

**Safety.** Exclusive streams and their COM state live on the endpoint's own audio thread exactly like the shared
ones; the stall detector, fault latch, and health tick apply unchanged.

## 6. Order of work

| Phase | Items | Ships as | Acceptance (run on this setup, record in the log) |
|---|---|---|---|
| A | L1, L2, L3; `LatencyBudget` tests for measured cadence; loopback keep-alive | v0.5.1 | AUTO settles once and stays for 30 min with no "auto headroom +" after the first minute; VB routes ≈ 61, Realtek loopback ≈ 44; UNDERRUNS flat |
| B | L4 per-input targets; coordinator fill-surplus barrier; route tiles show per-input totals | v0.5.2 | Intel-mic route ≈ 26 while a VB loopback is also routed; barrier releases within 2 s; no post-start skip |
| C0 | Feasibility probe tool (`--probe-exclusive`): opens each eligible output exclusive at its minimum period, streams 5 s of silence, reports underruns; opens the VB mic exclusive and reports its gap distribution | with C | every sink sustains 3 ms with 0 underruns; VB mic cadence under exclusive known |
| C | L5 toggle as specified; README section | v0.6.0 | toggle on → tiers read Exclusive 3 ms, loopback route ≈ 43, Intel-mic route ≈ 11; toggle off restores shared; refusal paths show their reason; TV sleep/wake drill ×10 passes with exclusive on |
| D | L6, L7 measured; mixed exclusive/shared outputs via delay compensation | later | only if the measurements justify it |

Phase A is the one that changes what the user experiences today: the real operating point moves from
64 to 84 ms down to a stable 61 (VB) or 44 (hardware loopback) with no code on the exclusive path at all.
