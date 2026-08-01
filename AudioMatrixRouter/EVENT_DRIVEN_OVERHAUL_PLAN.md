# Event-Driven Capture and Playback Overhaul Plan

## Objective

Migrate the audio engine to a true event-driven Windows WASAPI model for capture and playback, eliminate polling-delay behavior, and preserve hotplug/dormant-route stability.

## Scope

- Replace any polling or timer-dependent capture/render flow
- Use WASAPI event callback mode where possible
- Strengthen device arrival/removal handling with Windows notifications
- Preserve route persistence and dormant-route restore behavior
- Keep UI/native sync consistent during hotplug

## Implementation Phases

### Phase 1: Audit current engine
- [x] Review existing `AudioEngine` capture/render lifecycle
- [x] Identify timer/polling constructs in current audio path
- [x] Document event-driven readiness points and gaps

#### Phase 1 findings
- `AudioEngine` already uses `WasapiCapture(..., true, ...)` and `WasapiOut(..., true, ...)`, which are NAudio event-sync wrappers.
- `DeviceEnumerator` already implements `IMMNotificationClient`, so audio device hotplug notifications are already wired.
- No explicit sleep/polling patterns were found in the audio engine source.
- The main remaining risk is implicit state handling around device removal/restore and the current shared `AudioEngine` lifecycle.

### Phase 2: Separate capture/render abstractions
- [x] Add explicit `EventDrivenCaptureDevice` (implemented)
- [x] Add explicit `EventDrivenRenderDevice` (implemented)
- [x] Move WASAPI initialization into dedicated services (engine now delegates capture/render creation)
- [x] Keep `ActiveDevice` metadata but abstract NAudio objects

### Phase 3: Implement WASAPI event-driven flow
- [x] Validate current `WasapiCapture`/`WasapiOut` event-mode semantics
- [x] Convert remaining internal assumptions to explicit event callbacks via wrappers
- [x] Process capture buffer immediately on event
- [x] Fill render buffer immediately on event

### Phase 4: Harden ring/queue and overflow handling
- [x] Ensure ring writes/read only occur on audio events
- [x] Preserve ring headroom and overflow accounting
- [x] Keep `RoutingMatrix` lock-free on audio path

### Phase 5: Improve device hotplug
- [x] Restore dormant routes on reconnect before playback resumes
- [x] Capture routes on removal before device teardown
- [x] Never lose tile settings through device cycles

## Current status

- [x] Drafted plan file
- [x] Complete Phase 1 audit
- [x] Begin Phase 2 abstraction design
- [x] Implement Phase 2 WASAPI abstraction refactor
- [x] Begin Phase 3 event-driven flow cleanup
- [x] Complete Phase 3 event-driven flow cleanup
- [x] Complete Phase 4 ring/queue hardening
- [x] Complete Phase 5 hotplug/dormant route hardening

## Summary

The audio engine now uses dedicated capture and render wrapper classes for event-driven WASAPI flow, the ring buffer remains event-driven, and device hotplug is handled via notifications with dormant route preservation. No polling or sleep-based audio path logic was found in the current engine.

## Notes

This document will be updated with implementation progress, findings, and code changes as we iterate through the overhaul.
