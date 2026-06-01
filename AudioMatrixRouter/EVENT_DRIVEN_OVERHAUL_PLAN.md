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
- [ ] Review existing `AudioEngine` capture/render lifecycle
- [ ] Identify timer/polling constructs in current audio path
- [ ] Document event-driven readiness points and gaps

### Phase 2: Separate capture/render abstractions
- [ ] Add explicit `EventDrivenCaptureDevice`
- [ ] Add explicit `EventDrivenRenderDevice`
- [ ] Move WASAPI initialization into dedicated services
- [ ] Keep `ActiveDevice` metadata but abstract NAudio objects

### Phase 3: Implement WASAPI event-driven flow
- [ ] Initialize capture `AudioClient` with `AUDCLNT_STREAMFLAGS_EVENTCALLBACK`
- [ ] Initialize render `AudioClient` the same way
- [ ] Wait on audio events instead of polling
- [ ] Process capture buffer immediately on event
- [ ] Fill render buffer immediately on event

### Phase 4: Harden ring/queue and overflow handling
- [ ] Ensure ring writes/read only occur on audio events
- [ ] Preserve ring headroom and overflow accounting
- [ ] Keep `RoutingMatrix` lock-free on audio path

### Phase 5: Improve device hotplug
- [ ] Use Windows audio device notifications instead of repeated enumeration
- [ ] Restore dormant routes on reconnect before playback resumes
- [ ] Capture routes on removal before device teardown
- [ ] Never lose tile settings through device cycles

## Current status

- [x] Drafted plan file
- [ ] Begin Phase 1 audit

## Notes

This document will be updated with implementation progress, findings, and code changes as we iterate through the overhaul.
