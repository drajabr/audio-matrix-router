import React, { useEffect, useMemo, useRef, useState } from "react";

const APP_VERSION = __APP_VERSION__;
const STORAGE_KEY = "audio-router-matrix-v3";
const DB_MIN = -60;
const DB_MAX = 12;
const GLOBAL_MUTE_GAIN_DB = -90;
const GLOBAL_GAIN_DRAG_PX_PER_STEP = 24;
const DEVICE_CELL_SIZE = 112;
const GRID_GAP_SIZE = 4;
const CHANNEL_CELL_SIZE = (DEVICE_CELL_SIZE - GRID_GAP_SIZE) / 2;
const DEFAULT_LABEL_SQUARE_SIZE = DEVICE_CELL_SIZE * 2;
const SOURCE_LABEL_MIN = 140;
const SOURCE_LABEL_MAX = 360;
const DEST_LABEL_MIN = 140;
const DEST_LABEL_MAX = 320;
const LABEL_SQUARE_MIN = Math.max(SOURCE_LABEL_MIN, DEST_LABEL_MIN);
const LABEL_SQUARE_MAX = Math.min(SOURCE_LABEL_MAX, DEST_LABEL_MAX);
const BACKGROUND_KEY = "amrBackgroundPreference";
const ACCENT_KEY = "amrAccentPreference";
const FONT_KEY = "amrFontPreference";
const FONT_SIZE_KEY = "amrFontSizePreference";
const UI_SCALE_KEY = "amrUiScalePreference";
const QUICK_CONTROLS_COLLAPSED_KEY = "amrQuickControlsCollapsed";
const POWER_ON_KEY = "amrPowerOn";
const CAPTURE_BUFFER_OPTIONS = Array.from({ length: 39 }, (_, i) => 10 + i * 5);
const CAPTURE_BUFFER_MIN = 10;
const CAPTURE_BUFFER_MAX = 200;
const CAPTURE_BUFFER_DEFAULT = 40;

const BACKGROUND_PRESETS = [
  { key: "black", bg: "#090909", surface: "#121212", panel: "#101010", border: "#2a2a2a", text: "#ececec", muted: "#9a9a9a", swatch: "#121212" },
  { key: "charcoal", bg: "#111111", surface: "#1a1a1a", panel: "#161616", border: "#333333", text: "#ececec", muted: "#a0a0a0", swatch: "#1a1a1a" },
  { key: "graphite", bg: "#1a1a1a", surface: "#262626", panel: "#202020", border: "#444444", text: "#efefef", muted: "#ababab", swatch: "#262626" },
  { key: "slate", bg: "#252525", surface: "#323232", panel: "#2c2c2c", border: "#585858", text: "#f2f2f2", muted: "#b8b8b8", swatch: "#323232" },
  { key: "stone", bg: "#383838", surface: "#4a4a4a", panel: "#414141", border: "#676767", text: "#f5f5f5", muted: "#c4c4c4", swatch: "#4a4a4a" },
  { key: "silver", bg: "#b3b3b3", surface: "#c6c6c6", panel: "#bbbbbb", border: "#8f8f8f", text: "#151515", muted: "#3c3c3c", swatch: "#c6c6c6" },
  { key: "white", bg: "#e6e6e6", surface: "#f2f2f2", panel: "#ebebeb", border: "#bdbdbd", text: "#121212", muted: "#4c4c4c", swatch: "#f2f2f2" },
];

const ACCENT_PRESETS = [
  { key: "black", accent: "#0b0b0b", accentHl: "#6b7280" },
  { key: "white", accent: "#e5e7eb", accentHl: "#ffffff" },
  { key: "slate", accent: "#334155", accentHl: "#94a3b8" },
  { key: "cobalt", accent: "#1d4ed8", accentHl: "#60a5fa" },
  { key: "ocean", accent: "#0f766e", accentHl: "#2dd4bf" },
  { key: "amber", accent: "#b45309", accentHl: "#f59e0b" },
  { key: "crimson", accent: "#b91c1c", accentHl: "#f87171" },
];

const FONT_PRESETS = [
  { key: "plus-jakarta", label: "P", family: '"Plus Jakarta Sans", "Segoe UI", sans-serif' },
  { key: "manrope", label: "M", family: '"Manrope", "Segoe UI", sans-serif' },
  { key: "sora", label: "S", family: '"Sora", "Segoe UI", sans-serif' },
  { key: "bahnschrift", label: "B", family: '"Bahnschrift", "Segoe UI", sans-serif' },
  { key: "segoe-ui", label: "U", family: '"Segoe UI", sans-serif' },
  { key: "consolas", label: "C", family: 'Consolas, "Segoe UI", monospace' },
  { key: "system", label: "U", family: 'system-ui, -apple-system, "Segoe UI", sans-serif' },
];

const FONT_SIZE_PRESETS = [
  { key: "1", label: "1", size: "14px" },
  { key: "2", label: "2", size: "15px" },
  { key: "3", label: "3", size: "16px" },
  { key: "4", label: "4", size: "17px" },
  { key: "5", label: "5", size: "18px" },
  { key: "6", label: "6", size: "20px" },
  { key: "7", label: "7", size: "22px" },
];

const UI_SCALE_PRESETS = [
  { key: "xxxs", label: "XXS", scale: 0.70 },
  { key: "xxs", label: "XS", scale: 0.78 },
  { key: "xs", label: "SM", scale: 0.86 },
  { key: "sm", label: "MD", scale: 0.93 },
  { key: "md", label: "LG", scale: 1.0 },
  { key: "lg", label: "XL", scale: 1.08 },
  { key: "xl", label: "XXL", scale: 1.16 },
];

function getDynamicLabelSquareMax(uiScale = 1) {
  if (typeof window === "undefined") return LABEL_SQUARE_MAX;
  const viewportWidth = window.visualViewport?.width ?? window.innerWidth ?? LABEL_SQUARE_MAX;
  const viewportHeight = window.visualViewport?.height ?? window.innerHeight ?? LABEL_SQUARE_MAX;
  const available = Math.min(viewportWidth, viewportHeight) / Math.max(uiScale, 0.65);
  return Math.max(LABEL_SQUARE_MAX, Math.round(available * 0.68));
}

function shapeMeterLevel(value) {
  return clamp(Math.pow(clamp(Number(value) || 0, 0, 1), 0.72), 0, 1);
}

function ensureNativeBridge() {
  if (typeof window === "undefined" || !window.chrome?.webview) return false;
  if (typeof window.__nativeBridgeInvoke === "function") return true;

  const pending = new Map();
  window.__nativeBridgeResolve = (id, result, error) => {
    const entry = pending.get(id);
    if (!entry) return;
    pending.delete(id);
    if (error) {
      entry.reject(new Error(String(error)));
      return;
    }
    entry.resolve(result);
  };

  window.__nativeBridgeInvoke = (method, params = {}) => {
    const id = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
    const payload = JSON.stringify({ id, method, params });
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      window.chrome.webview.postMessage(payload);
    });
  };

  // Receive fire-and-forget pushes from native (e.g. periodic state snapshots).
  // Uses PostWebMessageAsJson on the C# side, which delivers a parsed object here.
  if (!window.__nativeBridgePushBound) {
    window.__nativeBridgePushBound = true;
    window.chrome.webview.addEventListener("message", (event) => {
      const data = event?.data;
      if (!data || typeof data !== "object") return;
      if (data.kind === "native-state" && data.state) {
        // Merge cached available-device lists when this push omitted them (hot tick).
        if (!data.state.hasFullDeviceLists) {
          const cached = window.__lastNativeState;
          if (cached) {
            if (!data.state.availableInputs && cached.availableInputs) {
              data.state.availableInputs = cached.availableInputs;
            }
            if (!data.state.availableOutputs && cached.availableOutputs) {
              data.state.availableOutputs = cached.availableOutputs;
            }
          }
        }
        window.__lastNativeState = data.state;
        window.dispatchEvent(new CustomEvent("native-state", { detail: data.state }));
      }
    });
  }

  return true;
}

function dbToLinear(db) {
  if (db <= DB_MIN) return 0;
  return Math.pow(10, db / 20);
}

function linearToDb(linear) {
  if (linear <= 0.00001) return DB_MIN;
  return 20 * Math.log10(linear);
}

function clamp(value, min, max) {
  return Math.min(max, Math.max(min, value));
}

function getStoredIndex(key, presets, fallback = 0) {
  if (typeof window === "undefined") return fallback;
  const stored = localStorage.getItem(key);
  const index = presets.findIndex((preset) => preset.key === stored);
  return index >= 0 ? index : fallback;
}

function makeDefaultConnection() {
  return {
    on: false,
    muted: false,
    gainDb: 0,
    phaseInverted: false,
  };
}

function sanitizeConnection(connection) {
  const on = !!connection?.on;
  const gainDb = Number.isFinite(connection?.gainDb) ? clamp(connection.gainDb, DB_MIN, DB_MAX) : 0;
  return {
    on,
    muted: on ? !!connection?.muted : false,
    gainDb,
    phaseInverted: !!connection?.phaseInverted,
  };
}

function getCellKey(rowId, colId) {
  return `${rowId}::${colId}`;
}

function createMatrix(rows, cols, saved = {}) {
  const next = {};

  rows.forEach((row) => {
    cols.forEach((col) => {
      const key = getCellKey(row.id, col.id);
      const merged = {
        ...makeDefaultConnection(),
        ...(saved[key] || {}),
      };

      if (typeof merged.gain === "number" && typeof merged.gainDb !== "number") {
        merged.gainDb = clamp(linearToDb(merged.gain), DB_MIN, DB_MAX);
      }

      next[key] = sanitizeConnection(merged);
    });
  });

  return next;
}

function createLabelMap(devices, saved = {}, prefix) {
  const next = {};
  devices.forEach((device, i) => {
    next[device.deviceId] = saved[device.deviceId] || device.label || `${prefix} ${i + 1}`;
  });
  return next;
}

function outputDeviceFromColId(colId) {
  if (!colId) return "";
  if (colId.startsWith("dev:")) return colId.slice(4);
  if (colId.startsWith("ch:")) return colId.split(":")[1];
  return "";
}

function isLoopbackSelfRoute(inputDeviceId, outputDeviceId) {
  if (!inputDeviceId || !outputDeviceId) return false;
  if (!String(inputDeviceId).startsWith("loop:")) return false;
  return String(inputDeviceId).slice("loop:".length) === String(outputDeviceId);
}

function parseChannelId(id) {
  const match = String(id || "").match(/^ch:(.*):(\d+)$/);
  if (!match) return null;
  return {
    deviceId: match[1],
    channelIndex: Number(match[2]),
  };
}

function collectChannelIndexesByDevice(channelMatrix, pickSide) {
  const map = new Map();
  Object.keys(channelMatrix || {}).forEach((key) => {
    const [rowId, colId] = key.split("::");
    const id = pickSide === "row" ? rowId : colId;
    const parsed = parseChannelId(id);
    if (!parsed) return;

    const current = map.get(parsed.deviceId) || new Set();
    current.add(parsed.channelIndex);
    map.set(parsed.deviceId, current);
  });

  const ordered = new Map();
  map.forEach((set, deviceId) => {
    ordered.set(deviceId, [...set].sort((a, b) => a - b));
  });
  return ordered;
}

function buildDeviceToChannelRouteMatrix(inputChannelIndexes, outputChannelIndexes) {
  const inCount = inputChannelIndexes.length;
  const outCount = outputChannelIndexes.length;

  if (inCount === 0 || outCount === 0) return [];

  if (inCount === outCount) {
    return inputChannelIndexes.map((inIdx, i) => ({
      inChannel: inIdx,
      outChannel: outputChannelIndexes[i],
      gainOffsetDb: 0,
    }));
  }

  if (inCount < outCount) {
    return outputChannelIndexes.map((outChannel, outSlot) => {
      const inSlot = Math.floor((outSlot * inCount) / outCount);
      return {
        inChannel: inputChannelIndexes[inSlot],
        outChannel,
        gainOffsetDb: 0,
      };
    });
  }

  const bucketSizes = Array.from({ length: outCount }, () => 0);
  inputChannelIndexes.forEach((_, inSlot) => {
    const outSlot = Math.min(outCount - 1, Math.floor((inSlot * outCount) / inCount));
    bucketSizes[outSlot] += 1;
  });

  return inputChannelIndexes.map((inChannel, inSlot) => {
    const outSlot = Math.min(outCount - 1, Math.floor((inSlot * outCount) / inCount));
    const groupSize = Math.max(1, bucketSizes[outSlot]);
    return {
      inChannel,
      outChannel: outputChannelIndexes[outSlot],
      gainOffsetDb: linearToDb(1 / groupSize),
    };
  });
}

function convertDeviceMatrixToChannelMatrix(deviceMatrix, channelMatrix) {
  const nextChannel = {};
  Object.keys(channelMatrix || {}).forEach((key) => {
    nextChannel[key] = makeDefaultConnection();
  });

  const inputChannelsByDevice = collectChannelIndexesByDevice(channelMatrix, "row");
  const outputChannelsByDevice = collectChannelIndexesByDevice(channelMatrix, "col");

  Object.entries(deviceMatrix || {}).forEach(([key, deviceConnection]) => {
    const [rowId, colId] = key.split("::");
    const inputDeviceId = rowId?.startsWith("dev:") ? rowId.slice(4) : "";
    const outputDeviceId = colId?.startsWith("dev:") ? colId.slice(4) : "";
    if (!inputDeviceId || !outputDeviceId) return;

    const inChannels = inputChannelsByDevice.get(inputDeviceId) || [];
    const outChannels = outputChannelsByDevice.get(outputDeviceId) || [];
    if (inChannels.length === 0 || outChannels.length === 0) return;

    const routes = buildDeviceToChannelRouteMatrix(inChannels, outChannels);
    routes.forEach((route) => {
      const chRowId = `ch:${inputDeviceId}:${route.inChannel}`;
      const chColId = `ch:${outputDeviceId}:${route.outChannel}`;
      const chKey = getCellKey(chRowId, chColId);
      if (!(chKey in nextChannel)) return;

      nextChannel[chKey] = {
        ...makeDefaultConnection(),
        on: !!deviceConnection?.on,
        muted: !!deviceConnection?.on && !!deviceConnection?.muted,
        gainDb: clamp(
          (Number.isFinite(deviceConnection?.gainDb) ? deviceConnection.gainDb : 0) + route.gainOffsetDb,
          DB_MIN,
          DB_MAX,
        ),
      };
    });
  });

  return nextChannel;
}

function simplifyDeviceLabel(label = "") {
  return String(label).replace(/\s*\(.*?\)\s*/g, " ").replace(/\s+/g, " ").trim();
}

function getDeviceLabelParts(label = "") {
  const raw = String(label || "").trim();
  const firstParenIndex = raw.indexOf("(");
  const primary = (firstParenIndex > 0 ? raw.slice(0, firstParenIndex) : raw).trim() || simplifyDeviceLabel(raw) || raw;
  const matches = [...raw.matchAll(/\(([^)]+)\)/g)].map((m) => (m[1] || "").trim()).filter(Boolean);
  const hardware = matches[0] || "";
  const deviceRef = matches[1] || "";
  return {
    primary,
    hardware: hardware && hardware !== primary ? hardware : "",
    deviceRef,
  };
}

function collectConfiguredDeviceIds(matrixByView) {
  const configuredInputIds = new Set();
  const configuredOutputIds = new Set();

  Object.entries(matrixByView?.device || {}).forEach(([key, conn]) => {
    if (!conn?.on) return;
    const [rowId, colId] = key.split("::");
    if (rowId?.startsWith("dev:")) configuredInputIds.add(rowId.slice(4));
    if (colId?.startsWith("dev:")) configuredOutputIds.add(colId.slice(4));
  });

  Object.entries(matrixByView?.channel || {}).forEach(([key, conn]) => {
    if (!conn?.on) return;
    const [rowId, colId] = key.split("::");
    const rowParsed = parseChannelId(rowId);
    const colParsed = parseChannelId(colId);
    if (rowParsed?.deviceId) configuredInputIds.add(rowParsed.deviceId);
    if (colParsed?.deviceId) configuredOutputIds.add(colParsed.deviceId);
  });

  return { configuredInputIds, configuredOutputIds };
}

function extractRouteDeviceIds(key) {
  const [rowId, colId] = String(key || "").split("::");
  const rowParsed = rowId?.startsWith("dev:") ? { deviceId: rowId.slice(4) } : parseChannelId(rowId);
  const colParsed = colId?.startsWith("dev:") ? { deviceId: colId.slice(4) } : parseChannelId(colId);
  if (!rowParsed?.deviceId || !colParsed?.deviceId) return null;
  return {
    inputDeviceId: rowParsed.deviceId,
    outputDeviceId: colParsed.deviceId,
  };
}

function mergeDormantRoutes(visibleView, persistedView, activeInputIds, activeOutputIds) {
  const merged = { ...(visibleView || {}) };
  Object.entries(persistedView || {}).forEach(([key, conn]) => {
    if (key in merged) return;
    const routeIds = extractRouteDeviceIds(key);
    if (!routeIds) return;
    const keepDormant = !activeInputIds.has(routeIds.inputDeviceId) || !activeOutputIds.has(routeIds.outputDeviceId);
    if (!keepDormant) return;
    merged[key] = sanitizeConnection(conn);
  });
  return merged;
}

function mergeMatrixByViewWithDormantRoutes(visibleMatrixByView, persistedMatrixByView, inputs, outputs) {
  const activeInputIds = new Set((inputs || []).map((input) => input.deviceId).filter(Boolean));
  const activeOutputIds = new Set((outputs || []).map((output) => output.deviceId).filter(Boolean));
  return {
    device: mergeDormantRoutes(visibleMatrixByView?.device, persistedMatrixByView?.device, activeInputIds, activeOutputIds),
    channel: mergeDormantRoutes(visibleMatrixByView?.channel, persistedMatrixByView?.channel, activeInputIds, activeOutputIds),
  };
}

function buildMatrixByViewFromNativeState(state, deviceRows, deviceCols, channelRows, channelCols, masterGainOffsetDb = 0) {
  const next = {
    device: createMatrix(deviceRows, deviceCols, {}),
    channel: createMatrix(channelRows, channelCols, {}),
  };

  const nativeInputs = Array.isArray(state?.inputs) ? state.inputs : [];
  const nativeOutputs = Array.isArray(state?.outputs) ? state.outputs : [];
  const nativeRoutes = Array.isArray(state?.routes) ? state.routes : [];

  const resolveDeviceChannel = (devices, globalChannel) => {
    if (!Number.isFinite(globalChannel)) return null;
    for (const d of devices) {
      const offset = Number.isFinite(d?.offset) ? d.offset : 0;
      const channels = Math.max(1, Number.isFinite(d?.channels) ? Math.floor(d.channels) : 1);
      if (globalChannel >= offset && globalChannel < offset + channels) {
        return {
          deviceId: d.deviceId,
          localChannel: globalChannel - offset,
        };
      }
    }
    return null;
  };

  const deviceAggregates = new Map();

  nativeRoutes.forEach((route) => {
    const inRef = resolveDeviceChannel(nativeInputs, route?.inCh);
    const outRef = resolveDeviceChannel(nativeOutputs, route?.outCh);
    if (!inRef || !outRef || !inRef.deviceId || !outRef.deviceId) return;

    const gainDb = clamp((Number.isFinite(route?.gainDb) ? route.gainDb : 0) - masterGainOffsetDb, DB_MIN, DB_MAX);

    const channelKey = getCellKey(`ch:${inRef.deviceId}:${inRef.localChannel}`, `ch:${outRef.deviceId}:${outRef.localChannel}`);
    if (channelKey in next.channel) {
      next.channel[channelKey] = {
        ...makeDefaultConnection(),
        on: true,
        muted: false,
        gainDb: clamp(gainDb, DB_MIN, DB_MAX),
      };
    }

    const deviceKey = getCellKey(`dev:${inRef.deviceId}`, `dev:${outRef.deviceId}`);
    const aggregate = deviceAggregates.get(deviceKey) || { count: 0, gainDbSum: 0 };
    aggregate.count += 1;
    aggregate.gainDbSum += gainDb;
    deviceAggregates.set(deviceKey, aggregate);
  });

  deviceAggregates.forEach((aggregate, deviceKey) => {
    if (!(deviceKey in next.device)) return;
    next.device[deviceKey] = {
      ...makeDefaultConnection(),
      on: aggregate.count > 0,
      muted: false,
      gainDb: aggregate.count > 0 ? clamp(aggregate.gainDbSum / aggregate.count, DB_MIN, DB_MAX) : 0,
    };
  });

  return next;
}

function mergeNativeMatrixWithLocalFlags(prevMatrix, nextNativeMatrix) {
  const mergeView = (prevView, nextView) => {
    const merged = {};
    Object.keys(nextView || {}).forEach((key) => {
      const nativeConn = nextView[key] || makeDefaultConnection();
      const prevConn = prevView?.[key] || makeDefaultConnection();

      const keepMutedLocal = !!prevConn?.muted;
      const keepGainLocal = !!prevConn?.on && Number.isFinite(prevConn?.gainDb);
      const mergedConn = {
        ...nativeConn,
        phaseInverted: !!prevConn?.phaseInverted,
        muted: keepMutedLocal,
        gainDb: keepGainLocal ? prevConn.gainDb : nativeConn.gainDb,
      };

      if (keepMutedLocal) {
        // Native host has no dedicated mute/phase fields; keep local mute intent stable.
        mergedConn.on = true;
        if (Number.isFinite(prevConn?.gainDb)) {
          mergedConn.gainDb = prevConn.gainDb;
        }
      }

      merged[key] = mergedConn;
    });
    return merged;
  };

  return {
    device: mergeView(prevMatrix?.device, nextNativeMatrix?.device),
    channel: mergeView(prevMatrix?.channel, nextNativeMatrix?.channel),
  };
}

function matrixConnectionEqual(a, b) {
  const aConn = a || makeDefaultConnection();
  const bConn = b || makeDefaultConnection();
  return (
    !!aConn.on === !!bConn.on &&
    !!aConn.muted === !!bConn.muted &&
    !!aConn.phaseInverted === !!bConn.phaseInverted &&
    Math.abs((Number(aConn.gainDb) || 0) - (Number(bConn.gainDb) || 0)) < 0.001
  );
}

function matrixViewEqual(a, b) {
  const aKeys = Object.keys(a || {});
  const bKeys = Object.keys(b || {});
  if (aKeys.length !== bKeys.length) return false;
  for (const key of aKeys) {
    if (!(key in (b || {}))) return false;
    if (!matrixConnectionEqual(a[key], b[key])) return false;
  }
  return true;
}

function matrixByViewEqual(a, b) {
  return matrixViewEqual(a?.device, b?.device) && matrixViewEqual(a?.channel, b?.channel);
}

class AudioMatrixManager {
  constructor() {
    this.context = null;
    this.inputStreams = new Map();
    this.inputNodes = new Map();
    this.outputNodes = new Map();
    this.crosspoints = new Map();
    this.inputMeterAnalyzers = new Map();
    this.outputMeterAnalyzers = new Map();
    this.outputMeterSplitters = new Map();
    this.meterRanges = new WeakMap();
    this.initialized = false;
  }

  ensureContext() {
    if (!this.context) {
      const Ctx = window.AudioContext || window.webkitAudioContext;
      this.context = new Ctx({ latencyHint: "interactive" });
    }
    return this.context;
  }

  async resumeOnGesture() {
    const ctx = this.ensureContext();
    if (ctx.state !== "running") {
      await ctx.resume();
    }
    return ctx.state;
  }

  computeRms(analyzer) {
    const len = analyzer.fftSize;
    const data = new Uint8Array(len);
    analyzer.getByteTimeDomainData(data);

    let sum = 0;
    for (let i = 0; i < len; i += 1) {
      const normalized = (data[i] - 128) / 128;
      sum += normalized * normalized;
    }

    const rms = Math.sqrt(sum / len);
    const range = this.meterRanges.get(analyzer) || { floor: 0.0015, peak: 0.06 };
    const rise = 0.18;
    const decay = 0.999;

    if (rms > range.peak) {
      range.peak += (rms - range.peak) * rise;
    } else {
      range.peak = Math.max(rms, range.peak * decay);
    }

    if (rms < range.floor) {
      range.floor += (rms - range.floor) * 0.06;
    } else {
      range.floor = Math.min(rms, range.floor * 1.0002 + 0.00002);
    }

    const minRange = 0.045;
    const maxRange = 0.36;
    let dynamicRange = clamp(range.peak - range.floor, minRange, maxRange);
    range.peak = range.floor + dynamicRange;

    this.meterRanges.set(analyzer, range);
    return clamp((rms - range.floor) / dynamicRange, 0, 1);
  }

  getInputLevel(rowId) {
    const analyzer = this.inputMeterAnalyzers.get(rowId);
    if (!analyzer) return 0;
    return this.computeRms(analyzer);
  }

  getOutputLevel(colId) {
    const analyzer = this.outputMeterAnalyzers.get(colId);
    if (!analyzer) return 0;
    return this.computeRms(analyzer);
  }

  setCrosspointGain(viewMode, rowId, colId, linearGain) {
    const key = `${viewMode}::${rowId}::${colId}`;
    const gainNode = this.crosspoints.get(key);
    if (!gainNode) return;

    gainNode.gain.setTargetAtTime(clamp(linearGain, -2, 2), this.ensureContext().currentTime, 0.015);
  }

  async teardown() {
    this.crosspoints.forEach((gainNode) => {
      try {
        gainNode.disconnect();
      } catch (_) {
        // no-op
      }
    });

    this.inputNodes.forEach((entry) => {
      try {
        entry.source.disconnect();
        entry.splitter.disconnect();
      } catch (_) {
        // no-op
      }
    });

    this.outputNodes.forEach((entry) => {
      try {
        entry.node.disconnect();
        entry.postGain.disconnect();
        entry.monitorAudio.pause();
        entry.monitorAudio.srcObject = null;
      } catch (_) {
        // no-op
      }
    });

    this.inputStreams.forEach((stream) => {
      stream.getTracks().forEach((t) => t.stop());
    });

    this.inputStreams.clear();
    this.inputNodes.clear();
    this.outputNodes.clear();
    this.crosspoints.clear();
    this.inputMeterAnalyzers.clear();
    this.outputMeterAnalyzers.clear();
    this.outputMeterSplitters.forEach((splitter) => {
      try { splitter.disconnect(); } catch (_) {}
    });
    this.outputMeterSplitters.clear();
    this.meterRanges = new WeakMap();
    this.initialized = false;
  }

  async setup(inputs, outputs, viewMode) {
    const ctx = this.ensureContext();

    if (this.initialized) {
      await this.teardown();
    }

    for (const input of inputs) {
      try {
        const inputChannelCount = Math.max(1, Number.isFinite(input?.channels) ? Math.floor(input.channels) : 2);
        const stream = await navigator.mediaDevices.getUserMedia({
          audio: {
            deviceId: input.deviceId ? { exact: input.deviceId } : undefined,
            echoCancellation: false,
            noiseSuppression: false,
            autoGainControl: false,
          },
          video: false,
        });

        const source = ctx.createMediaStreamSource(stream);
  const splitter = ctx.createChannelSplitter(inputChannelCount);
        source.connect(splitter);

        const deviceAnalyzer = ctx.createAnalyser();
        deviceAnalyzer.fftSize = 512;
        source.connect(deviceAnalyzer);
        this.inputMeterAnalyzers.set(`dev:${input.deviceId}`, deviceAnalyzer);

        for (let ch = 0; ch < inputChannelCount; ch += 1) {
          const analyzer = ctx.createAnalyser();
          analyzer.fftSize = 512;
          splitter.connect(analyzer, ch);
          this.inputMeterAnalyzers.set(`ch:${input.deviceId}:${ch}`, analyzer);
        }

        this.inputStreams.set(input.deviceId, stream);
        this.inputNodes.set(input.deviceId, { source, splitter });
      } catch (error) {
        console.error(`Could not access input ${input.label}`, error);
      }
    }

    for (const output of outputs) {
      const outputChannelCount = Math.max(1, Number.isFinite(output?.channels) ? Math.floor(output.channels) : 2);
      const destination = ctx.createMediaStreamDestination();
      const monitorAnalyzer = ctx.createAnalyser();
      monitorAnalyzer.fftSize = 512;

      const postGain = ctx.createGain();
      postGain.gain.value = 1;

      let node;
      if (viewMode === "channel") {
        node = ctx.createChannelMerger(outputChannelCount);
      } else {
        node = ctx.createGain();
      }

      node.connect(postGain);
      postGain.connect(destination);
      postGain.connect(monitorAnalyzer);

      const monitorAudio = document.createElement("audio");
      monitorAudio.autoplay = true;
      monitorAudio.playsInline = true;
      monitorAudio.muted = false;
      monitorAudio.srcObject = destination.stream;

      if (typeof monitorAudio.setSinkId === "function" && output.deviceId) {
        try {
          await monitorAudio.setSinkId(output.deviceId);
        } catch (error) {
          console.warn(`setSinkId failed for output ${output.label}`, error);
        }
      }

      try {
        await monitorAudio.play();
      } catch (_) {
        // Browser may require user gesture. Resume path handles it.
      }

      this.outputNodes.set(output.deviceId, { node, postGain, monitorAudio });
      this.outputMeterAnalyzers.set(`dev:${output.deviceId}`, monitorAnalyzer);

      if (viewMode === "channel") {
        const outSplitter = ctx.createChannelSplitter(outputChannelCount);
        postGain.connect(outSplitter);
        for (let ch = 0; ch < outputChannelCount; ch += 1) {
          const analyzer = ctx.createAnalyser();
          analyzer.fftSize = 512;
          outSplitter.connect(analyzer, ch);
          this.outputMeterAnalyzers.set(`ch:${output.deviceId}:${ch}`, analyzer);
        }
        this.outputMeterSplitters.set(output.deviceId, outSplitter);
      } else {
        for (let ch = 0; ch < outputChannelCount; ch += 1) {
          this.outputMeterAnalyzers.set(`ch:${output.deviceId}:${ch}`, monitorAnalyzer);
        }
      }
    }

    if (viewMode === "device") {
      for (const input of inputs) {
        const inputNode = this.inputNodes.get(input.deviceId);
        if (!inputNode) continue;

        for (const output of outputs) {
          const outputNode = this.outputNodes.get(output.deviceId);
          if (!outputNode) continue;

          const gain = ctx.createGain();
          gain.gain.value = 0;
          inputNode.source.connect(gain);
          gain.connect(outputNode.node);

          this.crosspoints.set(`device::dev:${input.deviceId}::dev:${output.deviceId}`, gain);
        }
      }
    } else {
      for (const input of inputs) {
        const inputNode = this.inputNodes.get(input.deviceId);
        if (!inputNode) continue;

        const inputChannelCount = Math.max(1, Number.isFinite(input?.channels) ? Math.floor(input.channels) : 2);

        for (let inCh = 0; inCh < inputChannelCount; inCh += 1) {
          for (const output of outputs) {
            const outputNode = this.outputNodes.get(output.deviceId);
            if (!outputNode) continue;

            const outputChannelCount = Math.max(1, Number.isFinite(output?.channels) ? Math.floor(output.channels) : 2);

            for (let outCh = 0; outCh < outputChannelCount; outCh += 1) {
              const gain = ctx.createGain();
              gain.gain.value = 0;

              inputNode.splitter.connect(gain, inCh);
              gain.connect(outputNode.node, 0, outCh);

              const rowId = `ch:${input.deviceId}:${inCh}`;
              const colId = `ch:${output.deviceId}:${outCh}`;
              this.crosspoints.set(`channel::${rowId}::${colId}`, gain);
            }
          }
        }
      }
    }

    this.initialized = true;
  }
}

export default function App({ runtime = "web" }) {
  const managerRef = useRef(new AudioMatrixManager());
  const rafRef = useRef(null);
  const hasNativeBridge = ensureNativeBridge();

  const [contextState, setContextState] = useState("suspended");
  const [latencyMs, setLatencyMs] = useState(null);
  const [inputLatencyMs, setInputLatencyMs] = useState(null);
  const [outputLatencyMs, setOutputLatencyMs] = useState(null);
  const [bufferMs, setBufferMs] = useState(null);
  const [jitterMs, setJitterMs] = useState(null);
  const [clockKhz, setClockKhz] = useState(null);
  const [error, setError] = useState("");
  const [isReloadingDevices, setIsReloadingDevices] = useState(false);
  const [backgroundIndex, setBackgroundIndex] = useState(() => getStoredIndex(BACKGROUND_KEY, BACKGROUND_PRESETS, 0));
  const [accentIndex, setAccentIndex] = useState(() => getStoredIndex(ACCENT_KEY, ACCENT_PRESETS, Math.max(0, ACCENT_PRESETS.findIndex((p) => p.key === "white"))));
  const [fontIndex, setFontIndex] = useState(() => getStoredIndex(FONT_KEY, FONT_PRESETS, Math.max(0, FONT_PRESETS.findIndex((p) => p.key === "consolas"))));
  const [fontSizeIndex, setFontSizeIndex] = useState(() => getStoredIndex(FONT_SIZE_KEY, FONT_SIZE_PRESETS, 4));
  const [uiScaleIndex, setUiScaleIndex] = useState(() => getStoredIndex(UI_SCALE_KEY, UI_SCALE_PRESETS, Math.max(0, UI_SCALE_PRESETS.findIndex((p) => p.key === "md"))));
  const [captureBufferMs, setCaptureBufferMs] = useState(CAPTURE_BUFFER_DEFAULT);
  const [masterGainDb, setMasterGainDb] = useState(0);
  const [wheelVisualOffsetPx, setWheelVisualOffsetPx] = useState(0);
  const [isApplyingCaptureBuffer, setIsApplyingCaptureBuffer] = useState(false);
  const [nativeTotalLatencyMs, setNativeTotalLatencyMs] = useState(null);
  const [nativeRouteLatencyByCh, setNativeRouteLatencyByCh] = useState({});
  const [nativeInputChannelMeta, setNativeInputChannelMeta] = useState({});
  const [nativeOutputChannelMeta, setNativeOutputChannelMeta] = useState({});
  const [inputDeviceMode, setInputDeviceMode] = useState("both");
  const [controlsCollapsed, setControlsCollapsed] = useState(() => {
    if (typeof window === "undefined") return true;
    return localStorage.getItem(QUICK_CONTROLS_COLLAPSED_KEY) !== "0";
  });
  const [activeQuickPicker, setActiveQuickPicker] = useState("");
  const [startupAtBoot, setStartupAtBoot] = useState(false);
  const [showAllDevices, setShowAllDevices] = useState(true);
  const [locked, setLocked] = useState(false);
  const [powerOn, setPowerOn] = useState(() => {
    if (typeof window === "undefined") return true;
    return localStorage.getItem(POWER_ON_KEY) !== "0";
  });
  const [transientMuteAll, setTransientMuteAll] = useState(false);
  const [showResizeGuides, setShowResizeGuides] = useState(false);

  const [viewMode, setViewMode] = useState("device");
  const [inputs, setInputs] = useState([]);
  const [outputs, setOutputs] = useState([]);
  const [inputMasterId, setInputMasterId] = useState("");
  const [outputMasterId, setOutputMasterId] = useState("");

  const [inputLabels, setInputLabels] = useState({});
  const [outputLabels, setOutputLabels] = useState({});

  const [matrixByView, setMatrixByView] = useState({
    device: {},
    channel: {},
  });

  const [selectedCell, setSelectedCell] = useState(null);
  const [inputLevels, setInputLevels] = useState({});
  const [outputLevels, setOutputLevels] = useState({});
  const [dragSortState, setDragSortState] = useState({
    axis: "",
    draggedId: "",
    overId: "",
  });
  const [labelSizing, setLabelSizing] = useState({
    sourceWidth: clamp(DEFAULT_LABEL_SQUARE_SIZE, LABEL_SQUARE_MIN, LABEL_SQUARE_MAX),
    destinationHeight: clamp(DEFAULT_LABEL_SQUARE_SIZE, LABEL_SQUARE_MIN, LABEL_SQUARE_MAX),
  });

  const matrixRef = useRef(matrixByView);
  const modeRef = useRef(viewMode);
  const resizeRef = useRef({
    mode: null,
    startX: 0,
    startY: 0,
    startWidth: DEFAULT_LABEL_SQUARE_SIZE,
    startHeight: DEFAULT_LABEL_SQUARE_SIZE,
  });

  const devicesDiscoveredRef = useRef(false);
  const quickControlsRef = useRef(null);
  const matrixWrapRef = useRef(null);
  const matrixWheelScrollRef = useRef({ targetLeft: 0, targetTop: 0, rafId: 0 });
  const dragScrollRef = useRef({ tracking: false, dragging: false, startX: 0, startY: 0, scrollLeft: 0, scrollTop: 0, pointerId: 0, blockNextClick: false });
  const latencyLastRef = useRef(null);
  const latencyJitterRef = useRef(0);
  const localEditHoldUntilRef = useRef(0);
  const nativeSyncVersionRef = useRef(0);
  const persistQueueRef = useRef(Promise.resolve());
  const lastPersistedJsonRef = useRef("");
  const meterFrameRef = useRef({ lastTs: 0 });
  const inputAutoZoomRef = useRef(new Map());
  const outputAutoZoomRef = useRef(new Map());
  // Mirror channel-meta state in refs so sync effects can read latest offsets
  // without peak-level updates triggering the entire setCrosspoints re-run.
  const nativeInputChannelMetaRef = useRef({});
  const nativeOutputChannelMetaRef = useRef({});
  const nativeMeterLastUpdateRef = useRef(0);
  const jitterUiRef = useRef({ lastTs: 0, lastValue: null });
  const latencyUiRef = useRef({ lastTs: 0, lastValue: null });
  const latencySmoothRef = useRef(null);
  const latencyMissingSinceRef = useRef(0);
  const wheelDragRef = useRef(null);
  const wheelDragSuppressClickRef = useRef(false);
  const bufferDragRef = useRef(null);
  const bufferDragSuppressClickRef = useRef(false);
  const captureBufferApplyTimerRef = useRef(null);
  const pendingCaptureBufferRef = useRef(null);
  const masterGainSyncTimerRef = useRef(null);
  const masterGainDbRef = useRef(masterGainDb);

  useEffect(() => {
    masterGainDbRef.current = masterGainDb;
    if (!wheelDragRef.current) {
      setWheelVisualOffsetPx(Math.round(masterGainDb / 0.5) * GLOBAL_GAIN_DRAG_PX_PER_STEP);
    }
  }, [masterGainDb]);

  useEffect(() => {
    matrixRef.current = matrixByView;
  }, [matrixByView]);

  useEffect(() => {
    modeRef.current = viewMode;
  }, [viewMode]);

  const updateJitterDisplay = (nextJitter) => {
    if (!Number.isFinite(nextJitter)) {
      jitterUiRef.current = { lastTs: 0, lastValue: null };
      setJitterMs(null);
      return;
    }

    const rounded = Math.round(nextJitter * 10) / 10;
    const now = performance.now();
    const { lastTs, lastValue } = jitterUiRef.current;
    if (lastValue == null || Math.abs(rounded - lastValue) >= 0.4 || (now - lastTs) >= 320) {
      jitterUiRef.current = { lastTs: now, lastValue: rounded };
      setJitterMs(rounded);
    }
  };

  const updateLatencyDisplay = (nextLatency) => {
    const LATENCY_NULL_GRACE_MS = 6000;
    const LATENCY_MIN_UPDATE_MS = 900;
    const LATENCY_STEP_THRESHOLD_MS = 1.2;
    const LATENCY_EMA_ALPHA = 0.1;

    if (!Number.isFinite(nextLatency)) {
      const now = performance.now();
      if (!latencyMissingSinceRef.current) {
        latencyMissingSinceRef.current = now;
        return;
      }
      if ((now - latencyMissingSinceRef.current) < LATENCY_NULL_GRACE_MS) {
        return;
      }
      latencyUiRef.current = { lastTs: 0, lastValue: null };
      latencySmoothRef.current = null;
      setLatencyMs(null);
      return;
    }

    latencyMissingSinceRef.current = 0;

    const rounded = Math.round(nextLatency * 10) / 10;
    const smoothed = latencySmoothRef.current == null
      ? rounded
      : (latencySmoothRef.current * (1 - LATENCY_EMA_ALPHA) + rounded * LATENCY_EMA_ALPHA);
    latencySmoothRef.current = smoothed;

    const display = Math.round(smoothed * 10) / 10;
    const now = performance.now();
    const { lastTs, lastValue } = latencyUiRef.current;
    if (
      lastValue == null ||
      Math.abs(display - lastValue) >= LATENCY_STEP_THRESHOLD_MS ||
      (now - lastTs) >= LATENCY_MIN_UPDATE_MS
    ) {
      latencyUiRef.current = { lastTs: now, lastValue: display };
      setLatencyMs(display);
    }
  };

  const { configuredInputIds, configuredOutputIds } = useMemo(
    () => collectConfiguredDeviceIds(matrixByView),
    [matrixByView],
  );

  const routedInputs = useMemo(() => {
    const next = inputs.filter((d) => configuredInputIds.has(d.deviceId));
    return configuredInputIds.size > 0 ? next : [];
  }, [inputs, configuredInputIds]);

  const routedOutputs = useMemo(() => {
    const next = outputs.filter((d) => configuredOutputIds.has(d.deviceId));
    return configuredOutputIds.size > 0 ? next : [];
  }, [outputs, configuredOutputIds]);

  const visibleInputs = showAllDevices
    ? inputs
    : (routedInputs.length > 0 ? routedInputs : inputs);
  const visibleOutputs = showAllDevices
    ? outputs
    : (routedOutputs.length > 0 ? routedOutputs : outputs);

  const monitoredInputs = useMemo(() => {
    if (!hasNativeBridge) return inputs;
    if (locked) return routedInputs;
    return showAllDevices ? inputs : routedInputs;
  }, [hasNativeBridge, locked, showAllDevices, inputs, routedInputs]);

  const monitoredOutputs = useMemo(() => {
    if (!hasNativeBridge) return outputs;
    if (locked) return routedOutputs;
    return showAllDevices ? outputs : routedOutputs;
  }, [hasNativeBridge, locked, showAllDevices, outputs, routedOutputs]);

  useEffect(() => {
    const root = document.documentElement;
    const background = BACKGROUND_PRESETS[backgroundIndex] || BACKGROUND_PRESETS[0];
    const accent = ACCENT_PRESETS[accentIndex] || ACCENT_PRESETS[0];
    const font = FONT_PRESETS[fontIndex] || FONT_PRESETS[0];
    const fontSize = FONT_SIZE_PRESETS[fontSizeIndex] || FONT_SIZE_PRESETS[2];
    const uiScale = UI_SCALE_PRESETS[uiScaleIndex] || UI_SCALE_PRESETS[2];

    root.style.setProperty("--bg", background.bg);
    root.style.setProperty("--surface", background.surface);
    root.style.setProperty("--panel", background.panel);
    root.style.setProperty("--line", background.border);
    root.style.setProperty("--text", background.text);
    root.style.setProperty("--muted", background.muted);
    root.style.setProperty("--accent", accent.accent);
    root.style.setProperty("--accent-hl", accent.accentHl);
    root.style.setProperty("--font-family", font.family);
    root.style.setProperty("--font-size", fontSize.size);
    root.style.setProperty("--ui-scale", String(uiScale.scale));

    localStorage.setItem(BACKGROUND_KEY, background.key);
    localStorage.setItem(ACCENT_KEY, accent.key);
    localStorage.setItem(FONT_KEY, font.key);
    localStorage.setItem(FONT_SIZE_KEY, fontSize.key);
    localStorage.setItem(UI_SCALE_KEY, uiScale.key);

  }, [backgroundIndex, accentIndex, fontIndex, fontSizeIndex, uiScaleIndex]);

  useEffect(() => {
    localStorage.setItem(QUICK_CONTROLS_COLLAPSED_KEY, controlsCollapsed ? "1" : "0");
  }, [controlsCollapsed]);

  useEffect(() => {
    localStorage.setItem(POWER_ON_KEY, powerOn ? "1" : "0");
  }, [powerOn]);

  useEffect(() => {
    if (!hasNativeBridge) return;
    window.__nativeBridgeInvoke("getState", {})
      .then((state) => setLocked(!!state?.locked))
      .catch(() => {});
    window.__nativeBridgeInvoke("getStartupAtBoot")
      .then((value) => setStartupAtBoot(!!value))
      .catch(() => {});
  }, [hasNativeBridge]);

  useEffect(() => {
    if (!locked) return;
    setSelectedCell(null);
  }, [locked]);

  useEffect(() => {
    const onClick = (event) => {
      if (!quickControlsRef.current?.contains(event.target)) {
        setActiveQuickPicker("");
        setControlsCollapsed(true);
      }
    };

    const onKeyDown = (event) => {
      if (event.key === "Escape") {
        setActiveQuickPicker("");
        setControlsCollapsed(true);
      }
    };

    document.addEventListener("click", onClick);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("click", onClick);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, []);

  useEffect(() => {
    let cancelled = false;
    let inFlight = false;
    let lastPushTs = 0;

    const applyNativeState = (state) => {
      if (!state || cancelled) return;
      lastPushTs = performance.now();

      if (typeof state?.inputDeviceMode === "string" && state.inputDeviceMode) {
        setInputDeviceMode(state.inputDeviceMode);
      }

      if (Number.isFinite(state?.captureBufferMs)) {
        const pendingCaptureBuffer = pendingCaptureBufferRef.current;
        if (!Number.isFinite(pendingCaptureBuffer)) {
          setCaptureBufferMs(state.captureBufferMs);
        } else if (Math.abs(state.captureBufferMs - pendingCaptureBuffer) < 0.5) {
          pendingCaptureBufferRef.current = null;
          setCaptureBufferMs(state.captureBufferMs);
        }
      }

      const total = Number.isFinite(state?.totalLatencyMs) ? Number(state.totalLatencyMs) : null;
      setNativeTotalLatencyMs(total != null ? Math.round(total * 10) / 10 : null);
      updateLatencyDisplay(total);

      const routeLatency = {};
      (Array.isArray(state?.routes) ? state.routes : []).forEach((route) => {
        if (!Number.isFinite(route?.inCh) || !Number.isFinite(route?.outCh)) return;
        if (!Number.isFinite(route?.workingLatencyMs)) return;
        routeLatency[`${route.inCh}::${route.outCh}`] = Number(route.workingLatencyMs);
      });
      setNativeRouteLatencyByCh(routeLatency);

      const inMeta = {};
      (Array.isArray(state?.inputs) ? state.inputs : []).forEach((d) => {
        if (!d?.deviceId) return;
        const peakLevels = Array.isArray(d?.peakLevels)
          ? d.peakLevels.map((v) => clamp(Number(v) || 0, 0, 1))
          : [];
        inMeta[d.deviceId] = {
          offset: Number.isFinite(d?.offset) ? d.offset : 0,
          channels: Number.isFinite(d?.channels) ? d.channels : 0,
          sampleRate: Number.isFinite(d?.sampleRate) ? d.sampleRate : 0,
          driverLatencyMs: Number.isFinite(d?.driverLatencyMs) ? d.driverLatencyMs : 0,
          overflows: Number.isFinite(d?.overflows) ? d.overflows : 0,
          droppedFrames: Number.isFinite(d?.droppedFrames) ? d.droppedFrames : 0,
          peakLevels,
        };
      });
      nativeInputChannelMetaRef.current = inMeta;
      setNativeInputChannelMeta(inMeta);

      const outMeta = {};
      (Array.isArray(state?.outputs) ? state.outputs : []).forEach((d) => {
        if (!d?.deviceId) return;
        const peakLevels = Array.isArray(d?.peakLevels)
          ? d.peakLevels.map((v) => clamp(Number(v) || 0, 0, 1))
          : [];
        outMeta[d.deviceId] = {
          offset: Number.isFinite(d?.offset) ? d.offset : 0,
          channels: Number.isFinite(d?.channels) ? d.channels : 0,
          sampleRate: Number.isFinite(d?.sampleRate) ? d.sampleRate : 0,
          driverLatencyMs: Number.isFinite(d?.driverLatencyMs) ? d.driverLatencyMs : 0,
          underruns: Number.isFinite(d?.underruns) ? d.underruns : 0,
          peakLevels,
        };
      });
      nativeOutputChannelMetaRef.current = outMeta;
      setNativeOutputChannelMeta(outMeta);
      nativeMeterLastUpdateRef.current = performance.now();

      if (hasNativeBridge && inputs.length > 0 && outputs.length > 0) {
        const deviceRows = inputs.map((i) => ({ id: `dev:${i.deviceId}` }));
        const deviceCols = outputs.map((o) => ({ id: `dev:${o.deviceId}` }));
        const channelRows = inputs.flatMap((i) => {
          const ch = Math.max(1, Number.isFinite(i?.channels) ? Math.floor(i.channels) : 2);
          return Array.from({ length: ch }, (_, idx) => ({ id: `ch:${i.deviceId}:${idx}` }));
        });
        const channelCols = outputs.flatMap((o) => {
          const ch = Math.max(1, Number.isFinite(o?.channels) ? Math.floor(o.channels) : 2);
          return Array.from({ length: ch }, (_, idx) => ({ id: `ch:${o.deviceId}:${idx}` }));
        });

        const now = performance.now();
        if (now >= localEditHoldUntilRef.current) {
          const nextNativeMatrix = buildMatrixByViewFromNativeState(
            state,
            deviceRows,
            deviceCols,
            channelRows,
            channelCols,
            masterGainDbRef.current,
          );
          setMatrixByView((prev) => {
            const merged = mergeNativeMatrixWithLocalFlags(prev, nextNativeMatrix);
            if (matrixByViewEqual(prev, merged)) return prev;
            matrixRef.current = merged;
            return merged;
          });
        }

        const masterInput = (Array.isArray(state?.inputs) ? state.inputs : []).find((d) => d?.isMaster)?.deviceId || "";
        const masterOutput = (Array.isArray(state?.outputs) ? state.outputs : []).find((d) => d?.isMaster)?.deviceId || "";
        if (masterInput !== inputMasterId) setInputMasterId(masterInput);
        if (masterOutput !== outputMasterId) setOutputMasterId(masterOutput);
      }

      if (total != null) {
        if (latencyLastRef.current != null) {
          const delta = Math.abs(total - latencyLastRef.current);
          latencyJitterRef.current = latencyJitterRef.current === 0
            ? delta
            : latencyJitterRef.current * 0.82 + delta * 0.18;
          updateJitterDisplay(latencyJitterRef.current);
        } else {
          latencyJitterRef.current = 0;
          updateJitterDisplay(0);
        }
        latencyLastRef.current = total;
      }

      const sr = (() => {
        const inSr = Object.values(inMeta).find((m) => m.sampleRate)?.sampleRate;
        const outSr = Object.values(outMeta).find((m) => m.sampleRate)?.sampleRate;
        return outSr || inSr || null;
      })();
      setClockKhz(sr ? Math.round((sr / 1000) * 10) / 10 : null);
      setInputLatencyMs(null);
      setOutputLatencyMs(null);
      setBufferMs(null);
    };

    const poll = async () => {
      if (inFlight) return;
      inFlight = true;

      try {
        if (hasNativeBridge) {
          // If the native side recently pushed a state, skip the redundant fetch.
          if (performance.now() - lastPushTs < 800) {
            return;
          }
          const state = await window.__nativeBridgeInvoke("getState", {});
          applyNativeState(state);
          return;
        }

        const ctx = managerRef.current.context;
        if (ctx && ctx.state === "running") {
          const base = (ctx.baseLatency || 0) * 1000;
          const out = (ctx.outputLatency || 0) * 1000;
          const lat = base + out;

          updateLatencyDisplay(lat > 0 ? lat : null);
          setInputLatencyMs(base > 0 ? Math.round(base * 10) / 10 : null);
          setOutputLatencyMs(out > 0 ? Math.round(out * 10) / 10 : null);
          setBufferMs(base > 0 ? Math.round(base * 10) / 10 : null);
          setClockKhz(ctx.sampleRate ? Math.round((ctx.sampleRate / 1000) * 10) / 10 : null);

          if (latencyLastRef.current != null) {
            const delta = Math.abs(lat - latencyLastRef.current);
            latencyJitterRef.current = latencyJitterRef.current === 0
              ? delta
              : latencyJitterRef.current * 0.82 + delta * 0.18;
            updateJitterDisplay(latencyJitterRef.current);
          }
          latencyLastRef.current = lat;
        } else {
          updateLatencyDisplay(null);
          setInputLatencyMs(null);
          setOutputLatencyMs(null);
          setBufferMs(null);
          updateJitterDisplay(null);
          setClockKhz(null);
          setNativeTotalLatencyMs(null);
          setNativeRouteLatencyByCh({});
          latencyLastRef.current = null;
          latencyJitterRef.current = 0;
        }
      } catch (_) {
        if (!cancelled) {
          updateLatencyDisplay(null);
          setNativeTotalLatencyMs(null);
        }
      } finally {
        inFlight = false;
      }
    };

    const onNativeState = (event) => applyNativeState(event?.detail);
    if (hasNativeBridge) {
      window.addEventListener("native-state", onNativeState);
    }

    void poll();
    // Native: rely on engine push (~200ms). Web: poll WebAudio context.
    const intervalMs = hasNativeBridge ? 1000 : 250;
    const id = setInterval(() => { void poll(); }, intervalMs);
    return () => {
      cancelled = true;
      clearInterval(id);
      if (hasNativeBridge) {
        window.removeEventListener("native-state", onNativeState);
      }
    };
  }, [hasNativeBridge, inputs, outputs, inputMasterId, outputMasterId]);

  useEffect(() => {
    if (!hasNativeBridge) return undefined;

    const staleAfterMs = Math.max(300, Number(captureBufferMs) || 1);
    const intervalMs = Math.max(16, Math.min(100, Math.round(staleAfterMs / 2)));

    const id = setInterval(() => {
      if (nativeMeterLastUpdateRef.current <= 0) return;
      if ((performance.now() - nativeMeterLastUpdateRef.current) <= staleAfterMs) return;

      const currentInput = nativeInputChannelMetaRef.current || {};
      if (Object.keys(currentInput).length > 0) {
        const nextInput = {};
        let changed = false;
        Object.entries(currentInput).forEach(([deviceId, meta]) => {
          const prevPeaks = Array.isArray(meta?.peakLevels) ? meta.peakLevels : [];
          if (prevPeaks.some((v) => (Number(v) || 0) !== 0)) changed = true;
          nextInput[deviceId] = {
            ...(meta || {}),
            peakLevels: prevPeaks.length > 0 ? new Array(prevPeaks.length).fill(0) : [],
          };
        });
        if (changed) {
          nativeInputChannelMetaRef.current = nextInput;
          setNativeInputChannelMeta(nextInput);
        }
      }

      const currentOutput = nativeOutputChannelMetaRef.current || {};
      if (Object.keys(currentOutput).length > 0) {
        const nextOutput = {};
        let changed = false;
        Object.entries(currentOutput).forEach(([deviceId, meta]) => {
          const prevPeaks = Array.isArray(meta?.peakLevels) ? meta.peakLevels : [];
          if (prevPeaks.some((v) => (Number(v) || 0) !== 0)) changed = true;
          nextOutput[deviceId] = {
            ...(meta || {}),
            peakLevels: prevPeaks.length > 0 ? new Array(prevPeaks.length).fill(0) : [],
          };
        });
        if (changed) {
          nativeOutputChannelMetaRef.current = nextOutput;
          setNativeOutputChannelMeta(nextOutput);
        }
      }
    }, intervalMs);

    return () => clearInterval(id);
  }, [hasNativeBridge, captureBufferMs]);

  const rows = useMemo(() => {
    if (viewMode === "device") {
      return visibleInputs.map((input) => {
        const rawLabel = inputLabels[input.deviceId] || input.label;
        const parts = getDeviceLabelParts(rawLabel);
        const channelCount = Math.max(1, Number.isFinite(input?.channels) ? Math.floor(input.channels) : 2);
        return {
          id: `dev:${input.deviceId}`,
          label: parts.primary,
          hardwareLabel: parts.hardware,
          deviceRefLabel: parts.deviceRef,
          fullLabel: rawLabel,
          deviceId: input.deviceId,
          channelCount,
          isMaster: input.deviceId === inputMasterId,
        };
      });
    }

    return visibleInputs.flatMap((input) => {
      const rawLabel = inputLabels[input.deviceId] || input.label;
      const parts = getDeviceLabelParts(rawLabel);
      const channelCount = Math.max(1, Number.isFinite(input?.channels) ? Math.floor(input.channels) : 2);
      return Array.from({ length: channelCount }, (_, ch) => {
        const pairedIds = [];
        for (let p = 0; p < channelCount; p += 1) {
          if (p !== ch) pairedIds.push(`ch:${input.deviceId}:${p}`);
        }
        return {
          id: `ch:${input.deviceId}:${ch}`,
          pairedId: pairedIds[0] || `ch:${input.deviceId}:${ch}`,
          pairedIds,
          label: parts.primary,
          hardwareLabel: parts.hardware,
          deviceRefLabel: parts.deviceRef,
          fullLabel: rawLabel,
          deviceId: input.deviceId,
          isChannelStart: ch === 0,
          isChannelEnd: ch === channelCount - 1,
          channelIndex: ch,
          channelCount,
          isMaster: input.deviceId === inputMasterId,
        };
      });
    });
  }, [visibleInputs, inputLabels, viewMode, inputMasterId]);

  const cols = useMemo(() => {
    if (viewMode === "device") {
      return visibleOutputs.map((output) => {
        const rawLabel = outputLabels[output.deviceId] || output.label;
        const parts = getDeviceLabelParts(rawLabel);
        const channelCount = Math.max(1, Number.isFinite(output?.channels) ? Math.floor(output.channels) : 2);
        return {
          id: `dev:${output.deviceId}`,
          label: parts.primary,
          hardwareLabel: parts.hardware,
          deviceRefLabel: parts.deviceRef,
          fullLabel: rawLabel,
          outputDeviceId: output.deviceId,
          delayMs: Number.isFinite(output.delayMs) ? output.delayMs : 0,
          channelCount,
          isMaster: output.deviceId === outputMasterId,
        };
      });
    }

    return visibleOutputs.flatMap((output) => {
      const rawLabel = outputLabels[output.deviceId] || output.label;
      const parts = getDeviceLabelParts(rawLabel);
      const channelCount = Math.max(1, Number.isFinite(output?.channels) ? Math.floor(output.channels) : 2);
      return Array.from({ length: channelCount }, (_, ch) => {
        const pairedIds = [];
        for (let p = 0; p < channelCount; p += 1) {
          if (p !== ch) pairedIds.push(`ch:${output.deviceId}:${p}`);
        }
        return {
          id: `ch:${output.deviceId}:${ch}`,
          pairedId: pairedIds[0] || `ch:${output.deviceId}:${ch}`,
          pairedIds,
          label: parts.primary,
          hardwareLabel: parts.hardware,
          deviceRefLabel: parts.deviceRef,
          fullLabel: rawLabel,
          outputDeviceId: output.deviceId,
          delayMs: Number.isFinite(output.delayMs) ? output.delayMs : 0,
          isChannelStart: ch === 0,
          isChannelEnd: ch === channelCount - 1,
          channelIndex: ch,
          channelCount,
          isMaster: output.deviceId === outputMasterId,
        };
      });
    });
  }, [visibleOutputs, outputLabels, viewMode, outputMasterId]);

  // One base unit everywhere: device view is a span of channel-sized cells.
  const cellSize = CHANNEL_CELL_SIZE;

  const activeMatrix = matrixByView[viewMode] || {};

  const getDevicePairFromRoute = (rowId, colId) => {
    const rowParsed = parseChannelId(rowId);
    const colParsed = parseChannelId(colId);
    const inputDeviceId = rowId?.startsWith("dev:") ? rowId.slice(4) : rowParsed?.deviceId || "";
    const outputDeviceId = colId?.startsWith("dev:") ? colId.slice(4) : colParsed?.deviceId || "";
    return { inputDeviceId, outputDeviceId };
  };

  const pushInputMasterToNative = (deviceId) => {
    if (!hasNativeBridge || !deviceId) return;
    window.__nativeBridgeInvoke("setInputMasterDevice", { deviceId }).catch(() => {});
  };

  const pushOutputMasterToNative = (deviceId) => {
    if (!hasNativeBridge || !deviceId) return;
    window.__nativeBridgeInvoke("setOutputMasterDevice", { deviceId }).catch(() => {});
  };

  const syncConnectionToNative = async (mode, rowId, colId, connection) => {
    if (!hasNativeBridge) return;
    const syncVersion = ++nativeSyncVersionRef.current;
    const findInput = (id) => inputs.find((d) => d?.deviceId === id);
    const findOutput = (id) => outputs.find((d) => d?.deviceId === id);
    const getInputOffset = (id) => Number.isFinite(nativeInputChannelMetaRef.current[id]?.offset) ? nativeInputChannelMetaRef.current[id].offset : null;
    const getOutputOffset = (id) => Number.isFinite(nativeOutputChannelMetaRef.current[id]?.offset) ? nativeOutputChannelMetaRef.current[id].offset : null;

    const active = !!connection?.on;
    const routeGainDb = Number.isFinite(connection?.gainDb) ? connection.gainDb : 0;
    const baseGainDb = clamp(routeGainDb + masterGainDb, DB_MIN, DB_MAX);

    const routesPayload = [];

    if (mode === "channel") {
      const rowParsed = parseChannelId(rowId);
      const colParsed = parseChannelId(colId);
      if (!rowParsed || !colParsed) return;

      const inputDevice = findInput(rowParsed.deviceId);
      const outputDevice = findOutput(colParsed.deviceId);
      if (!inputDevice || !outputDevice) return;

      routesPayload.push({
        inDeviceId: rowParsed.deviceId,
        inChannel: rowParsed.channelIndex,
        outDeviceId: colParsed.deviceId,
        outChannel: colParsed.channelIndex,
        ...(getInputOffset(rowParsed.deviceId) != null ? { inCh: getInputOffset(rowParsed.deviceId) + rowParsed.channelIndex } : {}),
        ...(getOutputOffset(colParsed.deviceId) != null ? { outCh: getOutputOffset(colParsed.deviceId) + colParsed.channelIndex } : {}),
        active,
        gainDb: baseGainDb,
      });
    } else {
      const inputDeviceId = rowId?.startsWith("dev:") ? rowId.slice(4) : "";
      const outputDeviceId = colId?.startsWith("dev:") ? colId.slice(4) : "";
      if (!inputDeviceId || !outputDeviceId) return;

      const inputDevice = findInput(inputDeviceId);
      const outputDevice = findOutput(outputDeviceId);
      if (!inputDevice || !outputDevice) return;

      const inChannels = Array.from(
        { length: Math.max(1, Number.isFinite(inputDevice?.channels) ? inputDevice.channels : 0) },
        (_, i) => i,
      );
      const outChannels = Array.from(
        { length: Math.max(1, Number.isFinite(outputDevice?.channels) ? outputDevice.channels : 0) },
        (_, i) => i,
      );

      if (!active) {
        // Device tile OFF means kill every channel route for that device pair immediately.
        inChannels.forEach((inChannel) => {
          outChannels.forEach((outChannel) => {
            routesPayload.push({
              inDeviceId: inputDeviceId,
              inChannel,
              outDeviceId: outputDeviceId,
              outChannel,
              ...(getInputOffset(inputDeviceId) != null ? { inCh: getInputOffset(inputDeviceId) + inChannel } : {}),
              ...(getOutputOffset(outputDeviceId) != null ? { outCh: getOutputOffset(outputDeviceId) + outChannel } : {}),
              active: false,
              gainDb: 0,
            });
          });
        });
      } else {
        const routes = buildDeviceToChannelRouteMatrix(inChannels, outChannels);
        routes.forEach((route) => {
          routesPayload.push({
            inDeviceId: inputDeviceId,
            inChannel: route.inChannel,
            outDeviceId: outputDeviceId,
            outChannel: route.outChannel,
            ...(getInputOffset(inputDeviceId) != null ? { inCh: getInputOffset(inputDeviceId) + route.inChannel } : {}),
            ...(getOutputOffset(outputDeviceId) != null ? { outCh: getOutputOffset(outputDeviceId) + route.outChannel } : {}),
            active: true,
            gainDb: clamp(baseGainDb + route.gainOffsetDb, DB_MIN, DB_MAX),
          });
        });
      }
    }

    if (routesPayload.length > 0) {
      try {
        const refreshed = await window.__nativeBridgeInvoke("setCrosspoints", { routes: routesPayload });
        if (syncVersion === nativeSyncVersionRef.current) {
          localEditHoldUntilRef.current = 0;
          if (refreshed) {
            window.dispatchEvent(new CustomEvent("native-state", { detail: refreshed }));
          }
        }
      } catch (_) {
        // Backward-compatible fallback for hosts that only expose setCrosspoint.
        const fallbackResults = await Promise.all(routesPayload.map((route) => {
          const legacy = {
            inCh: Number.isFinite(route.inCh) ? route.inCh : (Number.isFinite(route.inChannel) ? route.inChannel : 0),
            outCh: Number.isFinite(route.outCh) ? route.outCh : (Number.isFinite(route.outChannel) ? route.outChannel : 0),
            active: !!route.active,
            gainDb: Number.isFinite(route.gainDb) ? route.gainDb : 0,
          };
          return window.__nativeBridgeInvoke("setCrosspoint", legacy);
        }));
        if (syncVersion === nativeSyncVersionRef.current) {
          localEditHoldUntilRef.current = 0;
          const refreshed = fallbackResults[fallbackResults.length - 1];
          if (refreshed) {
            window.dispatchEvent(new CustomEvent("native-state", { detail: refreshed }));
          }
        }
      }
    }
  };

  const setInputMaster = (deviceId, syncNative = true) => {
    if (!deviceId) return;
    setInputMasterId(deviceId);
    if (syncNative) pushInputMasterToNative(deviceId);
  };

  const setOutputMaster = (deviceId, syncNative = true) => {
    if (!deviceId) return;
    setOutputMasterId(deviceId);
    if (syncNative) pushOutputMasterToNative(deviceId);
  };

  const activeRoutes = useMemo(() => {
    const set = new Set();
    Object.entries(activeMatrix).forEach(([key, conn]) => {
      if (conn.on) {
        const [rowId, colId] = key.split("::");
        set.add(rowId);
        set.add(colId);
      }
    });
    return set;
  }, [activeMatrix]);

  useEffect(() => {
    const inputIds = new Set(inputs.map((d) => d.deviceId));
    const outputIds = new Set(outputs.map((d) => d.deviceId));
    let nextIn = inputMasterId;
    let nextOut = outputMasterId;
    if (inputMasterId && !inputIds.has(inputMasterId)) nextIn = "";
    if (outputMasterId && !outputIds.has(outputMasterId)) nextOut = "";
    // If either master just became empty, try to fill from any active route.
    if (!nextIn || !nextOut) {
      const deviceMatrix = matrixRef.current?.device || {};
      const fallback = findActiveMasterPair(deviceMatrix);
      if (fallback) {
        if (!nextIn && fallback.inputDeviceId && inputIds.has(fallback.inputDeviceId)) {
          nextIn = fallback.inputDeviceId;
          pushInputMasterToNative(nextIn);
        }
        if (!nextOut && fallback.outputDeviceId && outputIds.has(fallback.outputDeviceId)) {
          nextOut = fallback.outputDeviceId;
          pushOutputMasterToNative(nextOut);
        }
      }
    }
    if (nextIn !== inputMasterId) setInputMasterId(nextIn);
    if (nextOut !== outputMasterId) setOutputMasterId(nextOut);
  }, [inputs, outputs, inputMasterId, outputMasterId]);

  const masterDetailCell = (() => {
    if (!inputMasterId || !outputMasterId) return null;
    const rowId = viewMode === "channel" ? `ch:${inputMasterId}:0` : `dev:${inputMasterId}`;
    const colId = viewMode === "channel" ? `ch:${outputMasterId}:0` : `dev:${outputMasterId}`;
    const rowExists = rows.some((row) => row.id === rowId);
    const colExists = cols.some((col) => col.id === colId);
    return rowExists && colExists ? { rowId, colId } : null;
  })();

  const detailCell = selectedCell || masterDetailCell;
  const selectedKey = detailCell ? getCellKey(detailCell.rowId, detailCell.colId) : "";
  const selectedConnection = selectedKey ? activeMatrix[selectedKey] || makeDefaultConnection() : null;
  const isHoverDetail = !!selectedCell;

  const buildPersistedState = (overrides = {}) => ({
    backgroundKey: BACKGROUND_PRESETS[backgroundIndex]?.key,
    accentKey: ACCENT_PRESETS[accentIndex]?.key,
    fontKey: FONT_PRESETS[fontIndex]?.key,
    fontSizeKey: FONT_SIZE_PRESETS[fontSizeIndex]?.key,
    uiScaleKey: UI_SCALE_PRESETS[uiScaleIndex]?.key,
    captureBufferMs,
    masterGainDb,
    controlsCollapsed,
    showAllDevices,
    inputDeviceMode,
    powerOn,
    locked,
    inputLabels,
    outputLabels,
    inputMasterId,
    outputMasterId,
    viewMode,
    labelSizing,
    matrixByView: matrixRef.current,
    inputOrder: inputs.map((d) => d.deviceId),
    outputOrder: outputs.map((d) => d.deviceId),
    ...overrides,
  });

  const persistState = (next) => {
    let json = "";
    try {
      const payload = hasNativeBridge
        ? {
            backgroundKey: next?.backgroundKey,
            accentKey: next?.accentKey,
            fontKey: next?.fontKey,
            fontSizeKey: next?.fontSizeKey,
            uiScaleKey: next?.uiScaleKey,
            captureBufferMs: next?.captureBufferMs,
            masterGainDb: next?.masterGainDb,
            controlsCollapsed: next?.controlsCollapsed,
            showAllDevices: next?.showAllDevices,
            inputDeviceMode: next?.inputDeviceMode,
            powerOn: next?.powerOn,
            locked: next?.locked,
            inputLabels: next?.inputLabels,
            outputLabels: next?.outputLabels,
            inputMasterId: next?.inputMasterId,
            outputMasterId: next?.outputMasterId,
            viewMode: next?.viewMode,
            labelSizing: next?.labelSizing,
            matrixByView: next?.matrixByView,
            inputOrder: next?.inputOrder,
            outputOrder: next?.outputOrder,
          }
        : next;
      json = JSON.stringify(payload);
    } catch (_) {
      return;
    }

    try {
      localStorage.setItem(STORAGE_KEY, json);
    } catch (_) {
      // no-op
    }

    if (!hasNativeBridge || lastPersistedJsonRef.current === json) return;

    lastPersistedJsonRef.current = json;
    persistQueueRef.current = persistQueueRef.current
      .catch(() => {})
      .then(() => window.__nativeBridgeInvoke("setUiPreferences", { json }))
      .catch(() => {});
  };

  const loadPersistedState = async () => {
    if (hasNativeBridge) {
      try {
        const raw = await window.__nativeBridgeInvoke("getUiPreferences", {});
        if (typeof raw === "string" && raw.trim()) {
          lastPersistedJsonRef.current = raw;
          return JSON.parse(raw);
        }
      } catch (_) {
        // Fall through to local storage fallback.
      }
    }

    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (typeof raw === "string" && raw.trim()) {
        lastPersistedJsonRef.current = raw;
      }
      return raw ? JSON.parse(raw) : null;
    } catch (_) {
      return null;
    }
  };

  const resolveLinearGain = (connection) => {
    if (!connection.on || transientMuteAll) return 0;
    const direction = connection.phaseInverted ? -1 : 1;
    const routeDb = Number.isFinite(connection?.gainDb) ? connection.gainDb : 0;
    const effectiveDb = clamp(routeDb + masterGainDb, DB_MIN, DB_MAX);
    return dbToLinear(effectiveDb) * direction;
  };

  const applyMatrixToEngine = (mode, matrix) => {
    Object.entries(matrix).forEach(([key, connection]) => {
      const [rowId, colId] = key.split("::");
      managerRef.current.setCrosspointGain(mode, rowId, colId, resolveLinearGain(connection));
    });
  };

  const rebuildAudioGraph = async (mode, nextMatrixByView = matrixRef.current) => {
    // Native mode owns the audio graph in C#. Don't double-capture/play in the browser.
    if (hasNativeBridge) return;

    if (!powerOn) {
      await managerRef.current.teardown();
      setContextState("suspended");
      return;
    }

    if (monitoredInputs.length === 0 || monitoredOutputs.length === 0) return;

    await managerRef.current.setup(monitoredInputs, monitoredOutputs, mode);
    applyMatrixToEngine(mode, nextMatrixByView[mode] || {});

    try {
      const state = await managerRef.current.resumeOnGesture();
      setContextState(state);
    } catch (_) {
      // no-op
    }
  };

  const discoverDevices = async (forceRefreshNative = false) => {
    try {
      setIsReloadingDevices(true);
      setError("");
      const saved = await loadPersistedState();

      if (saved?.backgroundKey) {
        const backgroundMatch = BACKGROUND_PRESETS.findIndex((p) => p.key === saved.backgroundKey);
        if (backgroundMatch >= 0) setBackgroundIndex(backgroundMatch);
      }
      if (saved?.accentKey) {
        const accentMatch = ACCENT_PRESETS.findIndex((p) => p.key === saved.accentKey);
        if (accentMatch >= 0) setAccentIndex(accentMatch);
      }
      if (saved?.fontKey) {
        const fontMatch = FONT_PRESETS.findIndex((p) => p.key === saved.fontKey);
        if (fontMatch >= 0) setFontIndex(fontMatch);
      }
      if (saved?.fontSizeKey) {
        const fontSizeMatch = FONT_SIZE_PRESETS.findIndex((p) => p.key === saved.fontSizeKey);
        if (fontSizeMatch >= 0) setFontSizeIndex(fontSizeMatch);
      }
      if (saved?.uiScaleKey) {
        const uiScaleMatch = UI_SCALE_PRESETS.findIndex((p) => p.key === saved.uiScaleKey);
        if (uiScaleMatch >= 0) setUiScaleIndex(uiScaleMatch);
      }
      if (typeof saved?.controlsCollapsed === "boolean") setControlsCollapsed(saved.controlsCollapsed);
      setShowAllDevices(typeof saved?.showAllDevices === "boolean" ? saved.showAllDevices : true);
      if (typeof saved?.inputDeviceMode === "string" && saved.inputDeviceMode) {
        setInputDeviceMode(saved.inputDeviceMode);
      }
      if (typeof saved?.powerOn === "boolean") setPowerOn(saved.powerOn);
      if (Number.isFinite(saved?.captureBufferMs)) setCaptureBufferMs(saved.captureBufferMs);
      if (Number.isFinite(saved?.masterGainDb)) {
        const savedGain = clamp(saved.masterGainDb, DB_MIN, DB_MAX);
        setMasterGainDb(savedGain);
        masterGainDbRef.current = savedGain; // sync ref immediately so buildMatrixByViewFromNativeState below uses correct offset
      }
      if (!hasNativeBridge && typeof saved?.locked === "boolean") setLocked(saved.locked);

      let discoveredInputs = [];
      let discoveredOutputs = [];
      let nativeState = null;

      if (hasNativeBridge) {
        if (forceRefreshNative) {
          try {
            await window.__nativeBridgeInvoke("refreshDevices", {});
          } catch (_) {
            // Ignore refresh failures; we'll still query the latest state snapshot.
          }
        }

        const state = await window.__nativeBridgeInvoke("getState", {});
        const savedMode = (typeof saved?.inputDeviceMode === "string") ? saved.inputDeviceMode : "";
        if (savedMode && (savedMode === "input" || savedMode === "loopback" || savedMode === "both") && state?.inputDeviceMode !== savedMode) {
          nativeState = await window.__nativeBridgeInvoke("setInputDeviceMode", { mode: savedMode });
        } else {
          nativeState = state;
        }
        if (Number.isFinite(nativeState?.captureBufferMs)) {
          setCaptureBufferMs(nativeState.captureBufferMs);
        }
        if (typeof nativeState?.inputDeviceMode === "string" && nativeState.inputDeviceMode) {
          setInputDeviceMode(nativeState.inputDeviceMode);
        }

        const includeCapture = nativeState?.inputDeviceMode !== "loopback";
        const includeLoopback = nativeState?.inputDeviceMode !== "input";
        const inputMatchesMode = (d) => {
          const isLoopback = !!d?.isLoopback || String(d?.deviceId || "").startsWith("loop:");
          return isLoopback ? includeLoopback : includeCapture;
        };
        const nativeInputs = [
          ...(Array.isArray(nativeState?.availableInputs) ? nativeState.availableInputs : []),
          ...(Array.isArray(nativeState?.inputs) ? nativeState.inputs.filter(inputMatchesMode) : []),
        ];
        const nativeOutputs = [
          ...(Array.isArray(nativeState?.outputs) ? nativeState.outputs : []),
          ...(Array.isArray(nativeState?.availableOutputs) ? nativeState.availableOutputs : []),
        ];

        const uniqByDeviceId = (list) => {
          const map = new Map();
          list.forEach((d, i) => {
            if (!d?.deviceId || map.has(d.deviceId)) return;
            const ch = Number.isFinite(d?.channels) && d.channels > 0 ? Math.floor(d.channels) : 2;
            map.set(d.deviceId, {
              deviceId: d.deviceId,
              label: d.label || `Device ${i + 1}`,
              delayMs: Number.isFinite(d?.delayMs) ? d.delayMs : 0,
              channels: ch,
              isLoopback: !!d?.isLoopback || String(d.deviceId).startsWith("loop:"),
            });
          });
          return [...map.values()];
        };

        discoveredInputs = uniqByDeviceId(nativeInputs).map((d) => ({
          deviceId: d.deviceId,
          label: d.label,
          channels: d.channels,
          isLoopback: !!d.isLoopback,
        }));
        discoveredOutputs = uniqByDeviceId(nativeOutputs).map((d) => ({
          deviceId: d.deviceId,
          label: d.label,
          delayMs: d.delayMs,
          channels: d.channels,
        }));
      } else {
        await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
        const devices = await navigator.mediaDevices.enumerateDevices();

        const readInputChannelCount = async (deviceId) => {
          try {
            const stream = await navigator.mediaDevices.getUserMedia({
              audio: {
                deviceId: deviceId ? { exact: deviceId } : undefined,
                echoCancellation: false,
                noiseSuppression: false,
                autoGainControl: false,
              },
              video: false,
            });
            const track = stream.getAudioTracks()[0];
            const settings = track?.getSettings?.() || {};
            const capabilities = track?.getCapabilities?.() || {};
            const fromSettings = Number.isFinite(settings.channelCount) ? Math.floor(settings.channelCount) : 0;
            const fromCapsMax = Number.isFinite(capabilities?.channelCount?.max) ? Math.floor(capabilities.channelCount.max) : 0;
            const fromCapsMin = Number.isFinite(capabilities?.channelCount?.min) ? Math.floor(capabilities.channelCount.min) : 0;
            stream.getTracks().forEach((t) => t.stop());
            return Math.max(1, fromSettings || fromCapsMax || fromCapsMin || 2);
          } catch (_) {
            return 2;
          }
        };

        const inputCandidates = devices
          .filter((d) => d.kind === "audioinput" && d.deviceId !== "default" && d.deviceId !== "communications");
        const discoveredInputChannels = {};
        await Promise.all(inputCandidates.map(async (d) => {
          discoveredInputChannels[d.deviceId] = await readInputChannelCount(d.deviceId);
        }));

        discoveredInputs = devices
          .filter((d) => d.kind === "audioinput" && d.deviceId !== "default" && d.deviceId !== "communications")
          .map((d, i) => ({
            deviceId: d.deviceId,
            label: d.label || `Input ${i + 1}`,
            channels: Math.max(1, discoveredInputChannels[d.deviceId] || 2),
          }));

        discoveredOutputs = devices
          .filter((d) => d.kind === "audiooutput" && d.deviceId !== "default" && d.deviceId !== "communications")
          .map((d, i) => ({
            deviceId: d.deviceId,
            label: d.label || `Output ${i + 1}`,
            delayMs: 0,
            channels: 2,
          }));
      }

      const savedInputOrder = saved?.inputOrder || [];
      const savedOutputOrder = saved?.outputOrder || [];

      const sortByOrder = (list, order) => {
        if (!order.length) return list;
        const indexed = new Map(list.map((d) => [d.deviceId, d]));
        const sorted = order.flatMap((id) => (indexed.has(id) ? [indexed.get(id)] : []));
        const rest = list.filter((d) => !order.includes(d.deviceId));
        return [...sorted, ...rest];
      };

      const orderedInputs = sortByOrder(discoveredInputs, savedInputOrder);
      const orderedOutputs = sortByOrder(discoveredOutputs, savedOutputOrder);

      const currentMatrixByView = matrixRef.current || {};
      setInputs(orderedInputs);
      setOutputs(orderedOutputs);
      devicesDiscoveredRef.current = true;

      const nextInputLabels = createLabelMap(discoveredInputs, saved?.inputLabels || {}, "Input");
      const nextOutputLabels = createLabelMap(discoveredOutputs, saved?.outputLabels || {}, "Output");

      const deviceRows = orderedInputs.map((i) => ({ id: `dev:${i.deviceId}` }));
      const deviceCols = orderedOutputs.map((o) => ({ id: `dev:${o.deviceId}` }));

      const channelRows = orderedInputs.flatMap((i) => {
        const ch = Math.max(1, Number.isFinite(i?.channels) ? Math.floor(i.channels) : 2);
        return Array.from({ length: ch }, (_, idx) => ({ id: `ch:${i.deviceId}:${idx}` }));
      });

      const channelCols = orderedOutputs.flatMap((o) => {
        const ch = Math.max(1, Number.isFinite(o?.channels) ? Math.floor(o.channels) : 2);
        return Array.from({ length: ch }, (_, idx) => ({ id: `ch:${o.deviceId}:${idx}` }));
      });

      const persistedMatrixByView = {
        device: {
          ...(saved?.matrixByView?.device || saved?.matrixState || {}),
          ...(currentMatrixByView.device || {}),
        },
        channel: {
          ...(saved?.matrixByView?.channel || {}),
          ...(currentMatrixByView.channel || {}),
        },
      };
      const sourceDeviceMatrix = persistedMatrixByView.device;
      const sourceChannelMatrix = persistedMatrixByView.channel;

      const savedBackedMatrixByView = {
        device: createMatrix(deviceRows, deviceCols, sourceDeviceMatrix),
        channel: createMatrix(channelRows, channelCols, sourceChannelMatrix),
      };

      const nativeMatrixByView = hasNativeBridge
        ? buildMatrixByViewFromNativeState(
            nativeState,
            deviceRows,
            deviceCols,
            channelRows,
            channelCols,
            masterGainDbRef.current, // use ref — state update above is batched and not yet committed
          )
        : null;

      const nextMatrixByView = mergeMatrixByViewWithDormantRoutes(
        nativeMatrixByView || savedBackedMatrixByView,
        persistedMatrixByView,
        orderedInputs,
        orderedOutputs,
      );

      const nextViewMode = saved?.viewMode === "channel" ? "channel" : "device";
      const hasSavedLabelSizing = !!saved?.labelSizing;
      const fallbackSource = DEFAULT_LABEL_SQUARE_SIZE;
      const persistedUiScale = UI_SCALE_PRESETS.find((preset) => preset.key === saved?.uiScaleKey)?.scale
        ?? (UI_SCALE_PRESETS[uiScaleIndex] || UI_SCALE_PRESETS[2]).scale;
      const seedSquare = clamp(
        hasSavedLabelSizing
          ? saved?.labelSizing?.sourceWidth ?? saved?.labelSizing?.destinationHeight ?? fallbackSource
          : fallbackSource,
        LABEL_SQUARE_MIN,
        getDynamicLabelSquareMax(persistedUiScale),
      );
      const nextLabelSizing = {
        sourceWidth: seedSquare,
        destinationHeight: seedSquare,
      };

      setInputLabels(nextInputLabels);
      setOutputLabels(nextOutputLabels);
      setViewMode(nextViewMode);
      setLabelSizing(nextLabelSizing);
      setMatrixByView(nextMatrixByView);
      matrixRef.current = nextMatrixByView;
      modeRef.current = nextViewMode;

      const nativeInputMaster = hasNativeBridge
        ? ((Array.isArray(nativeState?.inputs) ? nativeState.inputs : []).find((d) => d?.isMaster)?.deviceId || "")
        : "";
      const nativeOutputMaster = hasNativeBridge
        ? ((Array.isArray(nativeState?.outputs) ? nativeState.outputs : []).find((d) => d?.isMaster)?.deviceId || "")
        : "";
      let nextInputMaster = hasNativeBridge
        ? nativeInputMaster
        : (orderedInputs.some((d) => d.deviceId === saved?.inputMasterId) ? saved?.inputMasterId || "" : "");
      let nextOutputMaster = hasNativeBridge
        ? nativeOutputMaster
        : (orderedOutputs.some((d) => d.deviceId === saved?.outputMasterId) ? saved?.outputMasterId || "" : "");
      // If no master resolved, auto-pick from any active route in the loaded matrix.
      if (!nextInputMaster || !nextOutputMaster) {
        const loadedDeviceMatrix = nextMatrixByView?.device || {};
        for (const [routeKey, conn] of Object.entries(loadedDeviceMatrix)) {
          if (!conn?.on) continue;
          const sep = routeKey.indexOf("::");
          if (sep < 0) continue;
          const rowPart = routeKey.slice(0, sep);
          const colPart = routeKey.slice(sep + 2);
          const inId = rowPart.startsWith("dev:") ? rowPart.slice(4) : "";
          const outId = colPart.startsWith("dev:") ? colPart.slice(4) : "";
          if (inId && !nextInputMaster && orderedInputs.some((d) => d.deviceId === inId)) nextInputMaster = inId;
          if (outId && !nextOutputMaster && orderedOutputs.some((d) => d.deviceId === outId)) nextOutputMaster = outId;
          if (nextInputMaster && nextOutputMaster) break;
        }
      }
      setInputMasterId(nextInputMaster);
      setOutputMasterId(nextOutputMaster);
      if (!hasNativeBridge) {
        if (nextInputMaster) pushInputMasterToNative(nextInputMaster);
        if (nextOutputMaster) pushOutputMasterToNative(nextOutputMaster);
      }

      const snapshot = buildPersistedState({
        backgroundKey: saved?.backgroundKey || BACKGROUND_PRESETS[backgroundIndex]?.key,
        accentKey: saved?.accentKey || ACCENT_PRESETS[accentIndex]?.key,
        fontKey: saved?.fontKey || FONT_PRESETS[fontIndex]?.key,
        fontSizeKey: saved?.fontSizeKey || FONT_SIZE_PRESETS[fontSizeIndex]?.key,
        uiScaleKey: saved?.uiScaleKey || UI_SCALE_PRESETS[uiScaleIndex]?.key,
        captureBufferMs: Number.isFinite(saved?.captureBufferMs) ? saved.captureBufferMs : captureBufferMs,
        masterGainDb: Number.isFinite(saved?.masterGainDb) ? clamp(saved.masterGainDb, DB_MIN, DB_MAX) : masterGainDb,
        controlsCollapsed: typeof saved?.controlsCollapsed === "boolean" ? saved.controlsCollapsed : controlsCollapsed,
        showAllDevices: typeof saved?.showAllDevices === "boolean" ? saved.showAllDevices : showAllDevices,
        powerOn: typeof saved?.powerOn === "boolean" ? saved.powerOn : powerOn,
        locked: hasNativeBridge ? !!nativeState?.locked : !!saved?.locked,
        inputLabels: nextInputLabels,
        outputLabels: nextOutputLabels,
        inputMasterId: nextInputMaster,
        outputMasterId: nextOutputMaster,
        viewMode: nextViewMode,
        labelSizing: nextLabelSizing,
        inputOrder: orderedInputs.map((d) => d.deviceId),
        outputOrder: orderedOutputs.map((d) => d.deviceId),
      });
      persistState(snapshot);

      const discoveredConfigured = collectConfiguredDeviceIds(nextMatrixByView);
      const discoveredRoutedInputs = orderedInputs.filter((d) => discoveredConfigured.configuredInputIds.has(d.deviceId));
      const discoveredRoutedOutputs = orderedOutputs.filter((d) => discoveredConfigured.configuredOutputIds.has(d.deviceId));
      // In web mode the browser is the audio engine — always wire up all enumerated devices so meters animate
      // for every tile, regardless of routing or showAllDevices. In native mode, the C# engine handles audio,
      // so skip the WebAudio graph entirely.
      const setupInputs = hasNativeBridge
        ? []
        : orderedInputs;
      const setupOutputs = hasNativeBridge
        ? []
        : orderedOutputs;

      if (!hasNativeBridge && powerOn && setupInputs.length > 0 && setupOutputs.length > 0) {
        await managerRef.current.setup(setupInputs, setupOutputs, nextViewMode);
        applyMatrixToEngine(nextViewMode, nextMatrixByView[nextViewMode]);

        try {
          const state = await managerRef.current.resumeOnGesture();
          setContextState(state);
        } catch (_) {
          // no-op
        }
      } else {
        await managerRef.current.teardown();
        setContextState("suspended");
      }
    } catch (e) {
      console.error(e);
      setError("Microphone permission is required to enumerate and route devices.");
    } finally {
      setIsReloadingDevices(false);
    }
  };

  const handleReloadDevices = async (event) => {
    event?.stopPropagation?.();
    setSelectedCell(null);

    try {
      await managerRef.current.teardown();
      if (managerRef.current.context) {
        await managerRef.current.context.close();
      }
    } catch (_) {
      // no-op
    }

    managerRef.current = new AudioMatrixManager();
    setContextState("suspended");
    await discoverDevices(true);
  };

  const handleToggleInputDeviceMode = async () => {
    if (!hasNativeBridge || locked) return;
    const modes = ["input", "loopback", "both"];
    const currentIndex = modes.indexOf(inputDeviceMode);
    const nextMode = modes[(currentIndex + 1 + modes.length) % modes.length];
    try {
      await window.__nativeBridgeInvoke("setInputDeviceMode", { mode: nextMode });
      await discoverDevices(false);
    } catch (_) {
      // no-op
    }
  };

  const handleResetMatrix = async (event) => {
    event?.stopPropagation?.();

    if (hasNativeBridge) {
      try {
        localEditHoldUntilRef.current = 0;
        const state = await window.__nativeBridgeInvoke("clearRoutes", {});
        if (state) {
          window.dispatchEvent(new CustomEvent("native-state", { detail: state }));
        }
      } catch (_) {
        // no-op
      }
    }

    const deviceRows = inputs.map((input) => ({ id: `dev:${input.deviceId}` }));
    const deviceCols = outputs.map((output) => ({ id: `dev:${output.deviceId}` }));
    const channelRows = inputs.flatMap((input) => {
      const count = Math.max(1, Number.isFinite(input?.channels) ? Math.floor(input.channels) : 2);
      return Array.from({ length: count }, (_, idx) => ({ id: `ch:${input.deviceId}:${idx}` }));
    });
    const channelCols = outputs.flatMap((output) => {
      const count = Math.max(1, Number.isFinite(output?.channels) ? Math.floor(output.channels) : 2);
      return Array.from({ length: count }, (_, idx) => ({ id: `ch:${output.deviceId}:${idx}` }));
    });

    const nextMatrixByView = {
      device: createMatrix(deviceRows, deviceCols, {}),
      channel: createMatrix(channelRows, channelCols, {}),
    };

    setMatrixByView(nextMatrixByView);
    matrixRef.current = nextMatrixByView;
    setSelectedCell(null);

    persistState({
      ...buildPersistedState(),
      matrixByView: nextMatrixByView,
      inputOrder: inputs.map((d) => d.deviceId),
      outputOrder: outputs.map((d) => d.deviceId),
    });

    if (inputs.length > 0 && outputs.length > 0) {
      await rebuildAudioGraph(viewMode, nextMatrixByView);
    }
  };

  useEffect(() => {
    discoverDevices();

    const onDeviceChange = () => {
      discoverDevices();
    };

    navigator.mediaDevices?.addEventListener?.("devicechange", onDeviceChange);

    return () => {
      navigator.mediaDevices?.removeEventListener?.("devicechange", onDeviceChange);
    };
  }, []);

  useEffect(() => {
    if (!devicesDiscoveredRef.current) return;
    if (powerOn) {
      rebuildAudioGraph(viewMode, matrixRef.current);
      return;
    }

    managerRef.current.teardown().catch(() => {});
    setContextState("suspended");
  }, [powerOn]);

  useEffect(() => {
    if (!devicesDiscoveredRef.current) return;
    if (!powerOn) return;
    if (monitoredInputs.length === 0 || monitoredOutputs.length === 0) return;
    rebuildAudioGraph(viewMode, matrixRef.current);
  }, [inputs, outputs, locked, showAllDevices, monitoredInputs, monitoredOutputs]);

  useEffect(() => {
    if (!devicesDiscoveredRef.current) return;

    persistState({
      ...buildPersistedState(),
      matrixByView,
      inputOrder: inputs.map((d) => d.deviceId),
      outputOrder: outputs.map((d) => d.deviceId),
    });
  }, [
    backgroundIndex,
    accentIndex,
    fontIndex,
    fontSizeIndex,
    uiScaleIndex,
    captureBufferMs,
    masterGainDb,
    controlsCollapsed,
    showAllDevices,
    powerOn,
    locked,
    inputs,
    outputs,
    inputLabels,
    outputLabels,
    inputMasterId,
    outputMasterId,
    viewMode,
    labelSizing,
    matrixByView,
  ]);

  useEffect(() => {
    const unlockOnGesture = async () => {
      try {
        const state = await managerRef.current.resumeOnGesture();
        setContextState(state);
      } catch (_) {
        // no-op
      }
    };

    const events = ["pointerdown", "keydown", "touchstart"];
    events.forEach((name) => window.addEventListener(name, unlockOnGesture, { once: true }));

    return () => {
      events.forEach((name) => window.removeEventListener(name, unlockOnGesture));
    };
  }, []);

  useEffect(() => {
    // Page must never wheel-scroll. Allow wheel scrolling only inside matrix list.
    const preventPageWheel = (event) => {
      const target = event.target instanceof Element ? event.target : null;
      const insideMatrix = !!target?.closest(".matrix-wrap");

      if (!insideMatrix) {
        event.preventDefault();
        event.stopPropagation();
      }
    };

    document.addEventListener("wheel", preventPageWheel, { passive: false, capture: true });
    return () => {
      document.removeEventListener("wheel", preventPageWheel, { capture: true });
    };
  }, []);

  useEffect(() => {
    // Handle matrix wheel natively so edge scrolling cannot leak to page scrolling.
    const wrap = matrixWrapRef.current;
    if (!wrap) return undefined;

    const scrollState = matrixWheelScrollRef.current;
    scrollState.targetLeft = wrap.scrollLeft;
    scrollState.targetTop = wrap.scrollTop;

    const animateScroll = () => {
      const dx = scrollState.targetLeft - wrap.scrollLeft;
      const dy = scrollState.targetTop - wrap.scrollTop;

      if (Math.abs(dx) < 0.5 && Math.abs(dy) < 0.5) {
        wrap.scrollLeft = scrollState.targetLeft;
        wrap.scrollTop = scrollState.targetTop;
        scrollState.rafId = 0;
        return;
      }

      wrap.scrollLeft += dx * 0.22;
      wrap.scrollTop += dy * 0.22;
      scrollState.rafId = requestAnimationFrame(animateScroll);
    };

    const onMatrixWheel = (event) => {
      const target = event.target instanceof Element ? event.target : null;
      const cornerValueControl = target?.closest(".corner-gain-wheel, .corner-control-btn--buffer");
      if (cornerValueControl) {
        event.preventDefault();
        return;
      }

      const tile = target?.closest(".cell");
      if (tile && tile.classList.contains("on")) {
        event.preventDefault();
        return;
      }

      event.preventDefault();
      event.stopPropagation();

      const maxLeft = Math.max(0, wrap.scrollWidth - wrap.clientWidth);
      const maxTop = Math.max(0, wrap.scrollHeight - wrap.clientHeight);

      const deltaModeScale = event.deltaMode === 1 ? 16 : (event.deltaMode === 2 ? wrap.clientHeight : 1);
      const baseLeft = scrollState.rafId ? scrollState.targetLeft : wrap.scrollLeft;
      const baseTop = scrollState.rafId ? scrollState.targetTop : wrap.scrollTop;
      const nextLeft = clamp(baseLeft + event.deltaX * deltaModeScale, 0, maxLeft);
      const nextTop = clamp(baseTop + event.deltaY * deltaModeScale, 0, maxTop);

      scrollState.targetLeft = nextLeft;
      scrollState.targetTop = nextTop;
      if (!scrollState.rafId) {
        scrollState.rafId = requestAnimationFrame(animateScroll);
      }
    };

    wrap.addEventListener("wheel", onMatrixWheel, { passive: false, capture: true });
    return () => {
      if (scrollState.rafId) {
        cancelAnimationFrame(scrollState.rafId);
        scrollState.rafId = 0;
      }
      wrap.removeEventListener("wheel", onMatrixWheel, { capture: true });
    };
  }, []);

  useEffect(() => {
    const wrap = matrixWrapRef.current;
    if (!wrap) return undefined;

    const syncHeaderOffsets = () => {
      wrap.style.setProperty("--matrix-scroll-x", `${Math.round(wrap.scrollLeft)}px`);
      wrap.style.setProperty("--matrix-scroll-y", `${Math.round(wrap.scrollTop)}px`);
    };

    syncHeaderOffsets();
    wrap.addEventListener("scroll", syncHeaderOffsets, { passive: true });

    return () => {
      wrap.removeEventListener("scroll", syncHeaderOffsets);
    };
  }, []);

  useEffect(() => {
    if (hasNativeBridge) return undefined;

    const tick = () => {
      const currentMode = modeRef.current;
      const currentMatrix = matrixRef.current[currentMode] || {};
      const activeSet = new Set();
      Object.entries(currentMatrix).forEach(([key, conn]) => {
        if (!conn.on || conn.muted || transientMuteAll) return;
        const [rowId, colId] = key.split("::");
        activeSet.add(rowId);
        activeSet.add(colId);
      });

      const nextInput = {};
      rows.forEach((row) => {
        nextInput[row.id] = activeSet.has(row.id) ? managerRef.current.getInputLevel(row.id) : 0;
      });

      if (currentMode === "device") {
        inputs.forEach((input) => {
          const channelCount = Math.max(1, Number.isFinite(input?.channels) ? Math.floor(input.channels) : 2);
          const deviceActive = activeSet.has(`dev:${input.deviceId}`);
          for (let ch = 0; ch < channelCount; ch += 1) {
            const id = `ch:${input.deviceId}:${ch}`;
            nextInput[id] = deviceActive ? managerRef.current.getInputLevel(id) : 0;
          }
        });
      }

      const nextOutput = {};
      cols.forEach((col) => {
        nextOutput[col.id] = activeSet.has(col.id) ? managerRef.current.getOutputLevel(col.id) : 0;
      });

      if (currentMode === "device") {
        outputs.forEach((output) => {
          const channelCount = Math.max(1, Number.isFinite(output?.channels) ? Math.floor(output.channels) : 2);
          const deviceActive = activeSet.has(`dev:${output.deviceId}`);
          for (let ch = 0; ch < channelCount; ch += 1) {
            const id = `ch:${output.deviceId}:${ch}`;
            nextOutput[id] = deviceActive ? managerRef.current.getOutputLevel(id) : 0;
          }
        });
      }

      setInputLevels(nextInput);
      setOutputLevels(nextOutput);
      rafRef.current = requestAnimationFrame(tick);
    };

    rafRef.current = requestAnimationFrame(tick);

    return () => {
      if (rafRef.current) cancelAnimationFrame(rafRef.current);
    };
  }, [hasNativeBridge, rows, cols, routedInputs, routedOutputs, transientMuteAll]);

  useEffect(() => {
    if (inputs.length === 0 || outputs.length === 0) return;

    rebuildAudioGraph(viewMode, matrixRef.current);
    setSelectedCell(null);
  }, [viewMode]);

  useEffect(() => {
    if (!powerOn) return;
    applyMatrixToEngine(viewMode, matrixRef.current[viewMode] || {});
  }, [transientMuteAll, powerOn, viewMode]);

  // Send transient mute to native engine — no lock check, mute must work even when UI is locked.
  useEffect(() => {
    if (!hasNativeBridge) return;
    window.__nativeBridgeInvoke("setTransientMuteAll", { muted: transientMuteAll }).catch(() => {});
  }, [transientMuteAll, hasNativeBridge]);

  useEffect(() => {
    if (!selectedCell) return;

    const rowExists = rows.some((row) => row.id === selectedCell.rowId);
    const colExists = cols.some((col) => col.id === selectedCell.colId);
    if (!rowExists || !colExists) {
      setSelectedCell(null);
    }
  }, [rows, cols, selectedCell]);

  useEffect(() => {
    return () => {
      if (captureBufferApplyTimerRef.current) {
        clearTimeout(captureBufferApplyTimerRef.current);
      }
      if (masterGainSyncTimerRef.current) {
        clearTimeout(masterGainSyncTimerRef.current);
      }
      managerRef.current.teardown();
    };
  }, []);

  useEffect(() => {
    const onKeyDown = (event) => {
      if (event.key === "Escape") {
        setSelectedCell(null);
      }
    };

    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, []);

  // Returns { inputDeviceId, outputDeviceId } from the first active device-level route in the
  // given matrix, or null if no active routes exist.
  const findActiveMasterPair = (deviceMatrix) => {
    for (const [routeKey, conn] of Object.entries(deviceMatrix)) {
      if (!conn?.on) continue;
      const sep = routeKey.indexOf("::");
      if (sep < 0) continue;
      const rowPart = routeKey.slice(0, sep);
      const colPart = routeKey.slice(sep + 2);
      const inId = rowPart.startsWith("dev:") ? rowPart.slice(4) : "";
      const outId = colPart.startsWith("dev:") ? colPart.slice(4) : "";
      if (inId && outId) return { inputDeviceId: inId, outputDeviceId: outId };
    }
    return null;
  };

  const updateConnection = (rowId, colId, updater) => {
    if (locked) return;

    if (hasNativeBridge) {
      // Hold native matrix overwrite long enough that native confirms the user's edit
      // before applyNativeState can flip it back. The native push cycle is ~200ms;
      // 600ms covers 3 cycles to give the bridge time to confirm the change.
      localEditHoldUntilRef.current = performance.now() + 600;
    }

    const key = getCellKey(rowId, colId);
    let shouldReloadForMasterSwitch = false;
    // Derive masters from any currently-active route when none is set, BEFORE blocking ops.
    let nextInputMasterId = inputMasterId;
    let nextOutputMasterId = outputMasterId;
    if (!nextInputMasterId || !nextOutputMasterId) {
      const deviceMatrix = matrixRef.current?.device || {};
      const fallback = findActiveMasterPair(deviceMatrix);
      if (fallback) {
        if (!nextInputMasterId && fallback.inputDeviceId) {
          nextInputMasterId = fallback.inputDeviceId;
          setInputMaster(fallback.inputDeviceId, true);
          shouldReloadForMasterSwitch = true;
        }
        if (!nextOutputMasterId && fallback.outputDeviceId) {
          nextOutputMasterId = fallback.outputDeviceId;
          setOutputMaster(fallback.outputDeviceId, true);
          shouldReloadForMasterSwitch = true;
        }
      }
    }

    // IMPORTANT: compute the next connection synchronously here using matrixRef (always current),
    // BEFORE calling setMatrixByView. React state updaters run lazily during render, so any
    // value assigned inside a setMatrixByView callback is NOT available on the next line —
    // which would make the syncConnectionToNative call below silently never fire.
    const preCurrentMatrix = matrixRef.current?.[viewMode] || {};
    const preCurrentConnection = preCurrentMatrix[key] || makeDefaultConnection();
    const preRawNext = typeof updater === "function" ? updater(preCurrentConnection) : updater;
    const updatedConnection = sanitizeConnection(preRawNext);

    setMatrixByView((prev) => {
      const currentModeMatrix = prev[viewMode] || {};
      const nextConnection = updatedConnection;

      if (nextConnection.on) {
        const pair = getDevicePairFromRoute(rowId, colId);
        if (!nextInputMasterId && pair.inputDeviceId) {
          setInputMaster(pair.inputDeviceId, true);
          nextInputMasterId = pair.inputDeviceId;
          shouldReloadForMasterSwitch = true;
        }
        if (!nextOutputMasterId && pair.outputDeviceId) {
          setOutputMaster(pair.outputDeviceId, true);
          nextOutputMasterId = pair.outputDeviceId;
          shouldReloadForMasterSwitch = true;
        }
      }

      const nextModeMatrix = {
        ...currentModeMatrix,
        [key]: nextConnection,
      };

      const next = {
        ...prev,
        [viewMode]: nextModeMatrix,
      };

      if (viewMode === "channel") {
        const rowParsed = parseChannelId(rowId);
        const colParsed = parseChannelId(colId);
        const inDev = rowParsed?.deviceId || "";
        const outDev = colParsed?.deviceId || "";

        if (inDev && outDev) {
          const inChannels = Math.max(1, Number.isFinite(inputs.find((d) => d.deviceId === inDev)?.channels)
            ? Math.floor(inputs.find((d) => d.deviceId === inDev).channels)
            : 2);
          const outChannels = Math.max(1, Number.isFinite(outputs.find((d) => d.deviceId === outDev)?.channels)
            ? Math.floor(outputs.find((d) => d.deviceId === outDev).channels)
            : 2);

          const channelKeys = [];
          for (let inCh = 0; inCh < inChannels; inCh += 1) {
            for (let outCh = 0; outCh < outChannels; outCh += 1) {
              channelKeys.push(getCellKey(`ch:${inDev}:${inCh}`, `ch:${outDev}:${outCh}`));
            }
          }

          const connections = channelKeys.map((k) => nextModeMatrix[k] || makeDefaultConnection());
          const anyOn = connections.some((c) => c.on);
          const allMuted = anyOn ? connections.every((c) => c.muted || !c.on) : false;
          const avgGainDb = connections.length > 0
            ? connections.reduce((acc, c) => acc + (Number.isFinite(c.gainDb) ? c.gainDb : 0), 0) / connections.length
            : 0;

          next.device = {
            ...(prev.device || {}),
            [getCellKey(`dev:${inDev}`, `dev:${outDev}`)]: {
              ...makeDefaultConnection(),
              on: anyOn,
              muted: allMuted,
              gainDb: avgGainDb,
            },
          };
        }
      } else {
        next.channel = convertDeviceMatrixToChannelMatrix(nextModeMatrix, prev.channel || {});
      }

      const deviceMatrix = next.device || {};
      const isInputDeviceUsed = (deviceId) =>
        Object.entries(deviceMatrix).some(([routeKey, conn]) => conn?.on && routeKey.startsWith(`dev:${deviceId}::`));
      const isOutputDeviceUsed = (deviceId) =>
        Object.entries(deviceMatrix).some(([routeKey, conn]) => conn?.on && routeKey.includes(`::dev:${deviceId}`));

      if (nextConnection.on) {
        const pair = getDevicePairFromRoute(rowId, colId);

        if (
          nextInputMasterId &&
          pair.inputDeviceId &&
          nextInputMasterId !== pair.inputDeviceId &&
          !isInputDeviceUsed(nextInputMasterId)
        ) {
          setInputMaster(pair.inputDeviceId, true);
          nextInputMasterId = pair.inputDeviceId;
          shouldReloadForMasterSwitch = true;
        }

        if (
          nextOutputMasterId &&
          pair.outputDeviceId &&
          nextOutputMasterId !== pair.outputDeviceId &&
          !isOutputDeviceUsed(nextOutputMasterId)
        ) {
          setOutputMaster(pair.outputDeviceId, true);
          nextOutputMasterId = pair.outputDeviceId;
          shouldReloadForMasterSwitch = true;
        }
      }

      // After this connection update, if still no master, pick from any active route.
      if (!nextInputMasterId || !nextOutputMasterId) {
        const fallback = findActiveMasterPair(next.device || {});
        if (fallback) {
          if (!nextInputMasterId && fallback.inputDeviceId) {
            setInputMaster(fallback.inputDeviceId, true);
            nextInputMasterId = fallback.inputDeviceId;
            shouldReloadForMasterSwitch = true;
          }
          if (!nextOutputMasterId && fallback.outputDeviceId) {
            setOutputMaster(fallback.outputDeviceId, true);
            nextOutputMasterId = fallback.outputDeviceId;
            shouldReloadForMasterSwitch = true;
          }
        }
      }

      matrixRef.current = next;
      managerRef.current.setCrosspointGain(viewMode, rowId, colId, resolveLinearGain(nextConnection));

      persistState({
        ...buildPersistedState(),
        inputMasterId: nextInputMasterId,
        outputMasterId: nextOutputMasterId,
        matrixByView: next,
      });

      return next;
    });

    if (hasNativeBridge) {
      syncConnectionToNative(viewMode, rowId, colId, updatedConnection).catch(() => {});
    }

    if (shouldReloadForMasterSwitch && powerOn) {
      rebuildAudioGraph(viewMode, matrixRef.current);
    }
  };

  const toggleCell = (rowId, colId) => {
    if (locked) return;

    if (selectedCell?.rowId === rowId && selectedCell?.colId === colId) {
      updateConnection(rowId, colId, (prev) => ({
        ...prev,
        on: !prev.on,
        muted: false,
      }));
    } else {
      setSelectedCell({ rowId, colId });
    }
  };

  const reorderDeviceList = (list, fromId, toId, pickId) => {
    const fromIndex = list.findIndex((item) => pickId(item) === fromId);
    const toIndex = list.findIndex((item) => pickId(item) === toId);

    if (fromIndex < 0 || toIndex < 0 || fromIndex === toIndex) return list;

    const next = [...list];
    const [moved] = next.splice(fromIndex, 1);
    next.splice(toIndex, 0, moved);
    return next;
  };

  const beginSortDrag = (axis, deviceId) => (event) => {
    if (locked) return;
    event.dataTransfer.effectAllowed = "move";
    setDragSortState({
      axis,
      draggedId: deviceId,
      overId: deviceId,
    });
  };

  const overSortTarget = (axis, deviceId) => (event) => {
    if (locked) return;
    if (dragSortState.axis !== axis || !dragSortState.draggedId) return;

    event.preventDefault();
    event.dataTransfer.dropEffect = "move";

    if (dragSortState.overId === deviceId) return;

    if (axis === "source") {
      setInputs((prev) => reorderDeviceList(prev, dragSortState.draggedId, deviceId, (d) => d.deviceId));
    }

    if (axis === "destination") {
      setOutputs((prev) => reorderDeviceList(prev, dragSortState.draggedId, deviceId, (d) => d.deviceId));
    }

    setDragSortState((prev) => ({
      ...prev,
      overId: deviceId,
    }));
  };

  const dropSortTarget = (axis) => (event) => {
    if (locked) return;
    if (dragSortState.axis !== axis || !dragSortState.draggedId) return;
    event.preventDefault();
    setDragSortState({ axis: "", draggedId: "", overId: "" });
  };

  const endSortDrag = (axis) => () => {
    if (dragSortState.axis !== axis || !dragSortState.draggedId) return;
    setDragSortState({ axis: "", draggedId: "", overId: "" });
  };

  const handleToggleViewMode = () => {
    if (locked) return;
    setViewMode((prevMode) => {
      const nextMode = prevMode === "channel" ? "device" : "channel";

      if (prevMode === "device" && nextMode === "channel") {
        setMatrixByView((prevMatrix) => {
          const currentChannelMatrix = prevMatrix.channel || {};
          const hasCustomChannelRouting = Object.values(currentChannelMatrix).some((conn) => conn?.on);
          const nextChannelMatrix = hasCustomChannelRouting
            ? currentChannelMatrix
            : convertDeviceMatrixToChannelMatrix(prevMatrix.device || {}, currentChannelMatrix);
          const nextMatrix = {
            ...prevMatrix,
            channel: nextChannelMatrix,
          };

          matrixRef.current = nextMatrix;
          persistState({
            ...buildPersistedState(),
            viewMode: nextMode,
            matrixByView: nextMatrix,
          });

          return nextMatrix;
        });
      } else {
        persistState({
          ...buildPersistedState(),
          viewMode: nextMode,
          matrixByView: matrixRef.current,
        });
      }

      return nextMode;
    });
  };

  const handleInputLabelDoubleClick = (rowId) => {
    if (locked) return;
    const sourceId = viewMode === "device" ? rowId.slice(4) : rowId.split(":")[1];
    setInputMaster(sourceId, true);
    if (powerOn) {
      rebuildAudioGraph(viewMode, matrixRef.current);
    }
  };

  const handleOutputLabelDoubleClick = (colId) => {
    if (locked) return;
    const sourceId = outputDeviceFromColId(colId);
    setOutputMaster(sourceId, true);
    if (powerOn) {
      rebuildAudioGraph(viewMode, matrixRef.current);
    }
  };

  const handleSetStartupAtBoot = async (enabled) => {
    setStartupAtBoot(enabled);
    if (!hasNativeBridge) return;
    try {
      const applied = await window.__nativeBridgeInvoke("setStartupAtBoot", { enabled });
      setStartupAtBoot(!!applied);
    } catch (_) {
      setStartupAtBoot((prev) => prev);
    }
  };

  const togglePowerState = async () => {
    const next = !powerOn;
    setPowerOn(next);
    if (hasNativeBridge) {
      window.__nativeBridgeInvoke(next ? "startEngine" : "stopEngine", {}).catch(() => {});
    }
  };

  const handleToggleLock = async () => {
    const nextLocked = !locked;
    setLocked(nextLocked);

    if (!hasNativeBridge) return;

    try {
      const state = await window.__nativeBridgeInvoke("setLocked", { locked: nextLocked });
      setLocked(!!state?.locked);
    } catch (_) {
      setLocked(!nextLocked);
    }
  };

  const cyclePicker = (pickerType, event) => {
    event.stopPropagation();
    setControlsCollapsed(false);
    setActiveQuickPicker((prev) => (prev === pickerType ? "" : pickerType));
  };

  const quickPickerOptions = (() => {
    if (activeQuickPicker === "background") return BACKGROUND_PRESETS;
    if (activeQuickPicker === "accent") return ACCENT_PRESETS;
    if (activeQuickPicker === "font") return FONT_PRESETS;
    if (activeQuickPicker === "fontSize") return FONT_SIZE_PRESETS;
    if (activeQuickPicker === "uiScale") return UI_SCALE_PRESETS;
    if (activeQuickPicker === "captureBuffer") return CAPTURE_BUFFER_OPTIONS.map((value) => ({ key: String(value), label: `${value}ms` }));
    if (activeQuickPicker === "startup") {
      return [
        { key: "enable", label: "ON" },
        { key: "disable", label: "OFF" },
      ];
    }
    return [];
  })();

  const activeQuickKey = (() => {
    if (activeQuickPicker === "background") return BACKGROUND_PRESETS[backgroundIndex]?.key;
    if (activeQuickPicker === "accent") return ACCENT_PRESETS[accentIndex]?.key;
    if (activeQuickPicker === "font") return FONT_PRESETS[fontIndex]?.key;
    if (activeQuickPicker === "fontSize") return FONT_SIZE_PRESETS[fontSizeIndex]?.key;
    if (activeQuickPicker === "uiScale") return UI_SCALE_PRESETS[uiScaleIndex]?.key;
    if (activeQuickPicker === "captureBuffer") return String(captureBufferMs);
    if (activeQuickPicker === "startup") return startupAtBoot ? "enable" : "disable";
    return "";
  })();

  const applyQuickSelection = (type, key) => {
    if (type === "background") setBackgroundIndex(Math.max(0, BACKGROUND_PRESETS.findIndex((p) => p.key === key)));
    if (type === "accent") setAccentIndex(Math.max(0, ACCENT_PRESETS.findIndex((p) => p.key === key)));
    if (type === "font") setFontIndex(Math.max(0, FONT_PRESETS.findIndex((p) => p.key === key)));
    if (type === "fontSize") setFontSizeIndex(Math.max(0, FONT_SIZE_PRESETS.findIndex((p) => p.key === key)));
    if (type === "uiScale") setUiScaleIndex(Math.max(0, UI_SCALE_PRESETS.findIndex((p) => p.key === key)));
    if (type === "captureBuffer") {
      if (locked) return;
      const next = Number(key);
      if (Number.isFinite(next)) {
        const selected = clamp(Math.round(next / 5) * 5, CAPTURE_BUFFER_MIN, CAPTURE_BUFFER_MAX);
        setCaptureBufferMs(selected);
        persistState(buildPersistedState({ captureBufferMs: selected }));

        if (hasNativeBridge) {
          pendingCaptureBufferRef.current = selected;
          if (captureBufferApplyTimerRef.current) {
            clearTimeout(captureBufferApplyTimerRef.current);
          }
          captureBufferApplyTimerRef.current = setTimeout(() => {
            const requested = pendingCaptureBufferRef.current;
            if (!Number.isFinite(requested)) return;
            setIsApplyingCaptureBuffer(true);
            window.__nativeBridgeInvoke("setCaptureBufferMs", { bufferMs: requested })
              .then((state) => {
                const applied = Number.isFinite(state?.captureBufferMs) ? state.captureBufferMs : requested;
                if (pendingCaptureBufferRef.current !== requested) return;
                pendingCaptureBufferRef.current = null;
                setCaptureBufferMs(applied);
                persistState(buildPersistedState({ captureBufferMs: applied }));
              })
              .catch(() => {})
              .finally(() => {
                setIsApplyingCaptureBuffer(false);
              });
          }, 500);
        }
      }
    }
    if (type === "startup") handleSetStartupAtBoot(key === "enable");
  };

  const adjustGainForCell = (rowId, colId, stepDb) => {
    if (locked || !inputMasterId || !outputMasterId) return;
    const key = getCellKey(rowId, colId);
    const state = activeMatrix[key] || makeDefaultConnection();
    const baseGainDb = Number.isFinite(state.gainDb) ? state.gainDb : 0;
    updateConnection(rowId, colId, {
      ...state,
      on: true,
      gainDb: clamp(Math.round((baseGainDb + stepDb) * 2) / 2, DB_MIN, DB_MAX),
    });
  };

  const applyGlobalGainDelta = (deltaDb) => {
    if (locked || !inputMasterId || !outputMasterId || !Number.isFinite(deltaDb) || Math.abs(deltaDb) < 0.001) return;
    setMasterGainDb((prev) => {
      const next = clamp(Math.round((prev + deltaDb) * 2) / 2, DB_MIN, DB_MAX);
      return Math.abs(next - prev) < 0.001 ? prev : next;
    });
  };

  const finishWheelDrag = () => {
    wheelDragRef.current = null;
    setWheelVisualOffsetPx(Math.round(masterGainDbRef.current / 0.5) * GLOBAL_GAIN_DRAG_PX_PER_STEP);
  };

  const applyBufferDelta = (deltaMs) => {
    if (locked || !Number.isFinite(deltaMs)) return;
    const snapped = clamp(Math.round((captureBufferMs + deltaMs) / 5) * 5, CAPTURE_BUFFER_MIN, CAPTURE_BUFFER_MAX);
    if (snapped === captureBufferMs) return;
    applyQuickSelection("captureBuffer", String(snapped));
  };

  const buildNativeRoutesPayload = (matrix, options = {}) => {
    const {
      includeMasterGain = true,
      muteAll = false,
      mode = viewMode,
    } = options;
    const findInput = (id) => inputs.find((d) => d?.deviceId === id);
    const findOutput = (id) => outputs.find((d) => d?.deviceId === id);
    const getInputOffset = (id) => Number.isFinite(nativeInputChannelMetaRef.current[id]?.offset) ? nativeInputChannelMetaRef.current[id].offset : null;
    const getOutputOffset = (id) => Number.isFinite(nativeOutputChannelMetaRef.current[id]?.offset) ? nativeOutputChannelMetaRef.current[id].offset : null;
    const routesPayload = [];

    Object.entries(matrix).forEach(([key, conn]) => {
      if (!conn?.on) return;
      const [rowId, colId] = key.split("::");
      const routeGainDb = Number.isFinite(conn?.gainDb) ? conn.gainDb : 0;
      const baseGainDb = includeMasterGain
        ? clamp(routeGainDb + masterGainDbRef.current, DB_MIN, DB_MAX)
        : routeGainDb;

      if (mode === "channel") {
        const rowParsed = parseChannelId(rowId);
        const colParsed = parseChannelId(colId);
        if (!rowParsed || !colParsed) return;
        routesPayload.push({
          inDeviceId: rowParsed.deviceId,
          inChannel: rowParsed.channelIndex,
          outDeviceId: colParsed.deviceId,
          outChannel: colParsed.channelIndex,
          ...(getInputOffset(rowParsed.deviceId) != null ? { inCh: getInputOffset(rowParsed.deviceId) + rowParsed.channelIndex } : {}),
          ...(getOutputOffset(colParsed.deviceId) != null ? { outCh: getOutputOffset(colParsed.deviceId) + colParsed.channelIndex } : {}),
          active: true,
          gainDb: muteAll ? GLOBAL_MUTE_GAIN_DB : baseGainDb,
        });
        return;
      }

      const inputDeviceId = rowId?.startsWith("dev:") ? rowId.slice(4) : "";
      const outputDeviceId = colId?.startsWith("dev:") ? colId.slice(4) : "";
      if (!inputDeviceId || !outputDeviceId) return;

      const inputDevice = findInput(inputDeviceId);
      const outputDevice = findOutput(outputDeviceId);
      if (!inputDevice || !outputDevice) return;

      const inChannels = Array.from(
        { length: Math.max(1, Number.isFinite(inputDevice?.channels) ? inputDevice.channels : 0) },
        (_, i) => i,
      );
      const outChannels = Array.from(
        { length: Math.max(1, Number.isFinite(outputDevice?.channels) ? outputDevice.channels : 0) },
        (_, i) => i,
      );

      const routes = buildDeviceToChannelRouteMatrix(inChannels, outChannels);
      routes.forEach((route) => {
        routesPayload.push({
          inDeviceId: inputDeviceId,
          inChannel: route.inChannel,
          outDeviceId: outputDeviceId,
          outChannel: route.outChannel,
          ...(getInputOffset(inputDeviceId) != null ? { inCh: getInputOffset(inputDeviceId) + route.inChannel } : {}),
          ...(getOutputOffset(outputDeviceId) != null ? { outCh: getOutputOffset(outputDeviceId) + route.outChannel } : {}),
          active: true,
          gainDb: muteAll ? GLOBAL_MUTE_GAIN_DB : clamp(baseGainDb + route.gainOffsetDb, DB_MIN, DB_MAX),
        });
      });
    });

    return routesPayload;
  };

  useEffect(() => {
    if (!devicesDiscoveredRef.current) return;

    if (!hasNativeBridge) {
      if (powerOn) {
        applyMatrixToEngine(viewMode, matrixRef.current[viewMode] || {});
      }
      return;
    }

    if (!powerOn) {
      return;
    }

    if (masterGainSyncTimerRef.current) {
      clearTimeout(masterGainSyncTimerRef.current);
    }

    const timeoutId = window.setTimeout(() => {
      const syncMatrix = matrixRef.current.channel || {};
      const syncMode = Object.values(syncMatrix).some((conn) => conn?.on) ? "channel" : viewMode;
      const sourceMatrix = syncMode === "channel" ? syncMatrix : (matrixRef.current[viewMode] || {});
      const routesPayload = buildNativeRoutesPayload(sourceMatrix, {
        includeMasterGain: true,
        muteAll: transientMuteAll,
        mode: syncMode,
      });

      if (routesPayload.length === 0) {
        return;
      }

      localEditHoldUntilRef.current = performance.now() + (transientMuteAll ? 120 : 220);

      window.__nativeBridgeInvoke("setCrosspoints", { routes: routesPayload })
        .then((refreshed) => {
          localEditHoldUntilRef.current = 0;
          if (refreshed) {
            window.dispatchEvent(new CustomEvent("native-state", { detail: refreshed }));
          }
        })
        .catch(async () => {
          if (routesPayload.length > 128) {
            localEditHoldUntilRef.current = 0;
            return;
          }

          let refreshed = null;
          for (const route of routesPayload) {
            const legacy = {
              inCh: Number.isFinite(route.inCh) ? route.inCh : (Number.isFinite(route.inChannel) ? route.inChannel : 0),
              outCh: Number.isFinite(route.outCh) ? route.outCh : (Number.isFinite(route.outChannel) ? route.outChannel : 0),
              active: !!route.active,
              gainDb: Number.isFinite(route.gainDb) ? route.gainDb : 0,
            };
            // Sequential fallback keeps UI responsive versus firing all calls at once.
            // eslint-disable-next-line no-await-in-loop
            refreshed = await window.__nativeBridgeInvoke("setCrosspoint", legacy);
          }
          localEditHoldUntilRef.current = 0;
          if (refreshed) {
            window.dispatchEvent(new CustomEvent("native-state", { detail: refreshed }));
          }
        });
    }, 0);

    return () => window.clearTimeout(timeoutId);
  }, [masterGainDb, transientMuteAll, hasNativeBridge, powerOn, viewMode, inputs, outputs]);

  const beginResizeSourceWidth = (event) => {
    event.preventDefault();
    event.stopPropagation();

    resizeRef.current = {
      mode: "source-width",
      startX: event.clientX,
      startY: event.clientY,
      startWidth: labelSizing.sourceWidth,
      startHeight: labelSizing.destinationHeight,
    };
  };

  const beginResizeDestinationHeight = (event) => {
    event.preventDefault();
    event.stopPropagation();

    resizeRef.current = {
      mode: "destination-height",
      startX: event.clientX,
      startY: event.clientY,
      startWidth: labelSizing.sourceWidth,
      startHeight: labelSizing.destinationHeight,
    };
  };

  useEffect(() => {
    const onMove = (event) => {
      const state = resizeRef.current;
      if (!state.mode) return;
      const dynamicLabelSquareMax = getDynamicLabelSquareMax((UI_SCALE_PRESETS[uiScaleIndex] || UI_SCALE_PRESETS[2]).scale);

      if (state.mode === "source-width") {
        const nextSquare = clamp(state.startWidth + (event.clientX - state.startX), LABEL_SQUARE_MIN, dynamicLabelSquareMax);
        setLabelSizing({
          sourceWidth: nextSquare,
          destinationHeight: nextSquare,
        });
        document.body.style.cursor = "nwse-resize";
      }

      if (state.mode === "destination-height") {
        const nextSquare = clamp(state.startHeight + (event.clientY - state.startY), LABEL_SQUARE_MIN, dynamicLabelSquareMax);
        setLabelSizing({
          sourceWidth: nextSquare,
          destinationHeight: nextSquare,
        });
        document.body.style.cursor = "nwse-resize";
      }
    };

    const onUp = () => {
      if (!resizeRef.current.mode) return;
      resizeRef.current.mode = null;
      document.body.style.cursor = "";

      persistState(buildPersistedState({ labelSizing, matrixByView }));
    };

    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);

    return () => {
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
      document.body.style.cursor = "";
    };
  }, [labelSizing, viewMode, inputLabels, outputLabels, inputMasterId, outputMasterId, matrixByView, uiScaleIndex]);

  const selectedRouteText = detailCell
    ? `${rows.find((r) => r.id === detailCell.rowId)?.label || "Input"} -> ${
        cols.find((c) => c.id === detailCell.colId)?.label || "Output"
      }`
    : "Select a square in the matrix";

  const uiScale = UI_SCALE_PRESETS[uiScaleIndex] || UI_SCALE_PRESETS[2];
  const scaledCellSize = Math.round(cellSize * uiScale.scale);
  const scaledSourceWidth = Math.round(labelSizing.sourceWidth * uiScale.scale);
  const scaledDestinationHeight = Math.round(labelSizing.destinationHeight * uiScale.scale);
  const matrixColumnCount = viewMode === "device"
    ? Math.max(1, cols.reduce((acc, col) => acc + Math.max(1, col.channelCount || 1), 0))
    : Math.max(cols.length, 1);
  const matrixRowCount = viewMode === "device"
    ? Math.max(1, rows.reduce((acc, row) => acc + Math.max(1, row.channelCount || 1), 0))
    : Math.max(rows.length, 1);

  const setCaptureBufferFromMenu = (next) => {
    if (locked) return;
    applyQuickSelection("captureBuffer", String(next));
  };

  const selectedSource = detailCell ? rows.find((r) => r.id === detailCell.rowId) : null;
  const selectedDestination = detailCell ? cols.find((c) => c.id === detailCell.colId) : null;
  const nativeMeterDataFresh = !hasNativeBridge
    || (
      nativeMeterLastUpdateRef.current > 0
      && (performance.now() - nativeMeterLastUpdateRef.current) <= Math.max(1, Number(captureBufferMs) || 1)
    );
  const autoScaleLevels = (levels, scaleMapRef, scaleKey) => {
    const source = Array.isArray(levels) ? levels : [];
    const values = source.map((v) => clamp(Number(v) || 0, 0, 1));
    if (values.length === 0) return values;

    const tracker = scaleMapRef.current.get(scaleKey) || { floor: 0.003, peak: 0.1 };
    const observedPeak = values.reduce((acc, val) => Math.max(acc, val), 0);
    const observedFloor = values.reduce((acc, val) => Math.min(acc, val), 1);

    if (observedPeak > tracker.peak) {
      tracker.peak += (observedPeak - tracker.peak) * 0.32;
    } else {
      tracker.peak = Math.max(observedPeak, tracker.peak * 0.975);
    }

    if (observedFloor < tracker.floor) {
      tracker.floor += (observedFloor - tracker.floor) * 0.18;
    } else {
      tracker.floor = Math.min(observedFloor, tracker.floor * 1.015 + 0.0002);
    }

    const dynamicRange = clamp(tracker.peak - tracker.floor, 0.035, 0.7);
    const nextTracker = {
      floor: tracker.floor,
      peak: tracker.floor + dynamicRange,
    };
    scaleMapRef.current.set(scaleKey, nextTracker);

    if (scaleMapRef.current.size > 256) {
      const firstKey = scaleMapRef.current.keys().next().value;
      if (firstKey != null) scaleMapRef.current.delete(firstKey);
    }

    return values.map((value) => clamp((value - nextTracker.floor) / dynamicRange, 0, 1));
  };

  const getRowSplitLevels = (row) => {
    if (!row) return [0, 0];
    const devId = row.deviceId || (row.id.startsWith("dev:") ? row.id.slice(4) : row.id.split(":")[1]);
    const ch = Math.max(1, Number.isFinite(row?.channelCount) ? Math.floor(row.channelCount) : 2);
    if (hasNativeBridge) {
      if (!nativeMeterDataFresh) return [0, 0];
      const peaks = nativeInputChannelMeta[devId]?.peakLevels;
      if (Array.isArray(peaks) && peaks.length) {
        const scaled = autoScaleLevels(peaks, inputAutoZoomRef, `in-split:${devId}`).map((value) => shapeMeterLevel(value));
        const first = scaled[0] ?? 0;
        const second = ch > 1 ? (scaled[1] ?? 0) : first;
        return [first, second];
      }
      return [0, 0];
    }
    const leftId = `ch:${devId}:0`;
    const rightId = `ch:${devId}:${Math.min(1, ch - 1)}`;
    const left = inputLevels[leftId] ?? managerRef.current.getInputLevel(leftId) ?? 0;
    const right = inputLevels[rightId] ?? managerRef.current.getInputLevel(rightId) ?? 0;
    const scaled = autoScaleLevels([left, ch > 1 ? right : left], inputAutoZoomRef, `in-split:${devId}`);
    return [
      shapeMeterLevel(scaled[0] ?? 0),
      shapeMeterLevel(scaled[1] ?? 0),
    ];
  };

  const getColSplitLevels = (col) => {
    if (!col) return [0, 0];
    const devId = col.outputDeviceId || outputDeviceFromColId(col.id);
    const ch = Math.max(1, Number.isFinite(col?.channelCount) ? Math.floor(col.channelCount) : 2);
    if (hasNativeBridge) {
      if (!nativeMeterDataFresh) return [0, 0];
      const peaks = nativeOutputChannelMeta[devId]?.peakLevels;
      if (Array.isArray(peaks) && peaks.length) {
        const scaled = autoScaleLevels(peaks, outputAutoZoomRef, `out-split:${devId}`).map((value) => shapeMeterLevel(value));
        const first = scaled[0] ?? 0;
        const second = ch > 1 ? (scaled[1] ?? 0) : first;
        return [first, second];
      }
      return [0, 0];
    }
    const leftId = `ch:${devId}:0`;
    const rightId = `ch:${devId}:${Math.min(1, ch - 1)}`;
    const left = outputLevels[leftId] ?? managerRef.current.getOutputLevel(leftId) ?? 0;
    const right = outputLevels[rightId] ?? managerRef.current.getOutputLevel(rightId) ?? 0;
    const scaled = autoScaleLevels([left, ch > 1 ? right : left], outputAutoZoomRef, `out-split:${devId}`);
    return [
      shapeMeterLevel(scaled[0] ?? 0),
      shapeMeterLevel(scaled[1] ?? 0),
    ];
  };

  // Per-channel level arrays sized to the device's actual channel count.
  const getRowChannelLevels = (row) => {
    if (!row) return [0];
    const ch = Math.max(1, row.channelCount || 2);
    const devId = row.deviceId || (row.id.startsWith("dev:") ? row.id.slice(4) : row.id.split(":")[1]);
    if (hasNativeBridge) {
      if (!nativeMeterDataFresh) return new Array(ch).fill(0);
      const peaks = nativeInputChannelMeta[devId]?.peakLevels;
      const out = new Array(ch).fill(0);
      if (Array.isArray(peaks)) {
        const scaled = autoScaleLevels(peaks, inputAutoZoomRef, `in:${devId}`).map((value) => shapeMeterLevel(value));
        for (let i = 0; i < ch; i += 1) out[i] = scaled[i] ?? 0;
      }
      return out;
    }
    const raw = Array.from({ length: ch }, (_, i) => {
      const id = `ch:${devId}:${i}`;
      return inputLevels[id] ?? managerRef.current.getInputLevel(id) ?? 0;
    });
    return autoScaleLevels(raw, inputAutoZoomRef, `in:${devId}`).map((value) => shapeMeterLevel(value));
  };

  const getColChannelLevels = (col) => {
    if (!col) return [0];
    const ch = Math.max(1, col.channelCount || 2);
    const devId = col.outputDeviceId || outputDeviceFromColId(col.id);
    if (hasNativeBridge) {
      if (!nativeMeterDataFresh) return new Array(ch).fill(0);
      const peaks = nativeOutputChannelMeta[devId]?.peakLevels;
      const out = new Array(ch).fill(0);
      if (Array.isArray(peaks)) {
        const scaled = autoScaleLevels(peaks, outputAutoZoomRef, `out:${devId}`).map((value) => shapeMeterLevel(value));
        for (let i = 0; i < ch; i += 1) out[i] = scaled[i] ?? 0;
      }
      return out;
    }
    const raw = Array.from({ length: ch }, (_, i) => {
      const id = `ch:${devId}:${i}`;
      return outputLevels[id] ?? managerRef.current.getOutputLevel(id) ?? 0;
    });
    return autoScaleLevels(raw, outputAutoZoomRef, `out:${devId}`).map((value) => shapeMeterLevel(value));
  };

  const getChannelAxisLabel = (channelCount, channelIndex) => {
    if (channelCount <= 1) return "M";
    if (channelCount === 2) return channelIndex === 0 ? "L" : "R";
    return String(channelIndex + 1);
  };

  const selectedSourceRawSplit = selectedSource ? getRowSplitLevels(selectedSource) : [0, 0];
  const selectedDestinationRawSplit = selectedDestination ? getColSplitLevels(selectedDestination) : [0, 0];
  const isConnectionAudible = (conn) => !!conn?.on && !conn?.muted && !transientMuteAll;
  const routeIsAudible = isConnectionAudible(selectedConnection);
  const routeGainLinear = routeIsAudible
    ? dbToLinear(Number.isFinite(selectedConnection?.gainDb) ? selectedConnection.gainDb : 0)
    : 0;

  let selectedSourceSplit = [0, 0];
  let selectedDestinationSplit = [0, 0];

  if (isHoverDetail && routeIsAudible && detailCell) {
    const rowParsed = parseChannelId(detailCell.rowId);
    const colParsed = parseChannelId(detailCell.colId);

    if (viewMode === "channel" && rowParsed && colParsed) {
      const inIdx = clamp(rowParsed.channelIndex, 0, 1);
      const outIdx = clamp(colParsed.channelIndex, 0, 1);
      const inputLevel = selectedSourceRawSplit[inIdx] ?? 0;

      selectedSourceSplit[inIdx] = inputLevel;
      selectedDestinationSplit[outIdx] = clamp(inputLevel * routeGainLinear, 0, 1);
    } else {
      const left = selectedSourceRawSplit[0] ?? 0;
      const right = selectedSourceRawSplit[1] ?? 0;
      selectedSourceSplit = [left, right];
      selectedDestinationSplit = [
        clamp(left * routeGainLinear, 0, 1),
        clamp(right * routeGainLinear, 0, 1),
      ];
    }
  } else if (!isHoverDetail && (selectedSource || selectedDestination)) {
    const sourceDeviceId = selectedSource?.deviceId || parseChannelId(selectedSource?.id || "")?.deviceId || "";
    const destinationDeviceId = selectedDestination?.outputDeviceId || outputDeviceFromColId(selectedDestination?.id || "");

    let hasRouteFromLeft = false;
    let hasRouteFromRight = false;
    let hasRouteToLeft = false;
    let hasRouteToRight = false;

    Object.entries(activeMatrix).forEach(([routeKey, conn]) => {
      if (!isConnectionAudible(conn)) return;

      const [rowId, colId] = routeKey.split("::");
      const rowParsed = parseChannelId(rowId);
      const colParsed = parseChannelId(colId);

      if (sourceDeviceId) {
        const routeSourceDeviceId = rowId.startsWith("dev:") ? rowId.slice(4) : rowParsed?.deviceId || "";
        if (routeSourceDeviceId === sourceDeviceId) {
          if (viewMode === "channel") {
            if (rowId === `ch:${sourceDeviceId}:0`) hasRouteFromLeft = true;
            if (rowId === `ch:${sourceDeviceId}:1`) hasRouteFromRight = true;
          } else {
            hasRouteFromLeft = true;
            hasRouteFromRight = true;
          }
        }
      }

      if (destinationDeviceId) {
        const routeDestinationDeviceId = outputDeviceFromColId(colId);
        if (routeDestinationDeviceId === destinationDeviceId) {
          if (viewMode === "channel") {
            if (colId === `ch:${destinationDeviceId}:0`) hasRouteToLeft = true;
            if (colId === `ch:${destinationDeviceId}:1`) hasRouteToRight = true;
          } else {
            hasRouteToLeft = true;
            hasRouteToRight = true;
          }
        }
      }
    });

    selectedSourceSplit = [
      hasRouteFromLeft ? selectedSourceRawSplit[0] ?? 0 : 0,
      hasRouteFromRight ? selectedSourceRawSplit[1] ?? 0 : 0,
    ];

    selectedDestinationSplit = [
      hasRouteToLeft ? selectedDestinationRawSplit[0] ?? 0 : 0,
      hasRouteToRight ? selectedDestinationRawSplit[1] ?? 0 : 0,
    ];
  }
  const hasAnyActiveRoute = Object.values(activeMatrix).some((conn) => conn.on);
  const routeIndicatorActive = !!selectedConnection?.on;

  const detailRouteLatencyMs = (() => {
    if (!hasNativeBridge || !detailCell) return null;

    const lookup = (inCh, outCh) => {
      const raw = nativeRouteLatencyByCh[`${inCh}::${outCh}`];
      return Number.isFinite(raw) ? raw : null;
    };

    if (viewMode === "channel") {
      const rowParsed = parseChannelId(detailCell.rowId);
      const colParsed = parseChannelId(detailCell.colId);
      if (!rowParsed || !colParsed) return null;

      const inMeta = nativeInputChannelMeta[rowParsed.deviceId];
      const outMeta = nativeOutputChannelMeta[colParsed.deviceId];
      if (!inMeta || !outMeta) return null;

      const inCh = inMeta.offset + rowParsed.channelIndex;
      const outCh = outMeta.offset + colParsed.channelIndex;
      return lookup(inCh, outCh);
    }

    const sourceDeviceId = detailCell.rowId.startsWith("dev:") ? detailCell.rowId.slice(4) : "";
    const destinationDeviceId = outputDeviceFromColId(detailCell.colId);
    const inMeta = nativeInputChannelMeta[sourceDeviceId];
    const outMeta = nativeOutputChannelMeta[destinationDeviceId];
    if (!inMeta || !outMeta) return null;

    let best = null;
    for (let inLocal = 0; inLocal < inMeta.channels; inLocal += 1) {
      const inCh = inMeta.offset + inLocal;
      for (let outLocal = 0; outLocal < outMeta.channels; outLocal += 1) {
        const outCh = outMeta.offset + outLocal;
        const value = lookup(inCh, outCh);
        if (value == null) continue;
        best = best == null ? value : Math.max(best, value);
      }
    }

    return best;
  })();

  const nativeTopbarTotalLatencyMs = hasNativeBridge
    ? nativeTotalLatencyMs
    : null;
  const runningLatencyDisplayMs = hasNativeBridge
    ? (nativeTopbarTotalLatencyMs ?? latencyMs)
    : latencyMs;
  const sourceLatencyDisplayMs = hasNativeBridge
    ? (detailRouteLatencyMs ?? inputLatencyMs)
    : inputLatencyMs;
  const latencyLabel = runningLatencyDisplayMs != null ? `${runningLatencyDisplayMs}ms` : "n/a";
  const selectedSourceDeviceId = selectedSource?.deviceId || parseChannelId(selectedSource?.id || "")?.deviceId || "";
  const selectedDestinationDeviceId = selectedDestination?.outputDeviceId || outputDeviceFromColId(selectedDestination?.id || "");
  const sourceDeviceDriverLatencyMs = Number.isFinite(nativeInputChannelMeta[selectedSourceDeviceId]?.driverLatencyMs)
    ? nativeInputChannelMeta[selectedSourceDeviceId].driverLatencyMs
    : null;
  const destinationDeviceDriverLatencyMs = Number.isFinite(nativeOutputChannelMeta[selectedDestinationDeviceId]?.driverLatencyMs)
    ? nativeOutputChannelMeta[selectedDestinationDeviceId].driverLatencyMs
    : null;
  const selectedDestinationDelayMs = Number.isFinite(selectedDestination?.delayMs) ? selectedDestination.delayMs : 0;
  const destinationLatencyResolvedMs =
    hasNativeBridge
      ? (detailRouteLatencyMs ?? (outputLatencyMs != null || selectedDestinationDelayMs > 0
        ? Math.round(((outputLatencyMs ?? 0) + selectedDestinationDelayMs) * 10) / 10
        : null))
      : (outputLatencyMs != null || selectedDestinationDelayMs > 0
        ? Math.round(((outputLatencyMs ?? 0) + selectedDestinationDelayMs) * 10) / 10
        : null)
  ;
  const sourceLatencyEffectiveMs = sourceLatencyDisplayMs ?? sourceDeviceDriverLatencyMs;
  const destinationLatencyEffectiveMs = destinationLatencyResolvedMs ?? (
    destinationDeviceDriverLatencyMs != null || selectedDestinationDelayMs > 0
      ? Math.round(((destinationDeviceDriverLatencyMs ?? 0) + selectedDestinationDelayMs) * 10) / 10
      : null
  );
  const sourceLatencyLabel = sourceLatencyEffectiveMs != null ? `${sourceLatencyEffectiveMs}ms` : "n/a";
  const destinationLatencyLabel = destinationLatencyEffectiveMs != null ? `${destinationLatencyEffectiveMs}ms` : "n/a";
  const jitterLabel = jitterMs != null ? `${jitterMs}ms` : "n/a";

  const selectedSourceChannelLabels = Array.from(
    { length: Math.max(1, Number.isFinite(selectedSource?.channelCount) ? selectedSource.channelCount : 1) },
    (_, i) => getChannelAxisLabel(Math.max(1, Number.isFinite(selectedSource?.channelCount) ? selectedSource.channelCount : 1), i),
  );
  const selectedDestinationChannelLabels = Array.from(
    { length: Math.max(1, Number.isFinite(selectedDestination?.channelCount) ? selectedDestination.channelCount : 1) },
    (_, i) => getChannelAxisLabel(Math.max(1, Number.isFinite(selectedDestination?.channelCount) ? selectedDestination.channelCount : 1), i),
  );
  const routeIndicatorMultiChannel = viewMode !== "channel"
    && Math.max(selectedSourceChannelLabels.length, selectedDestinationChannelLabels.length) > 1;
  const routeIndicatorIcon = routeIndicatorActive
    ? (routeIndicatorMultiChannel ? "⮆" : "🡢")
    : "⏸";
  const selectedRowPathIndex = selectedCell ? rows.findIndex((row) => row.id === selectedCell.rowId) : -1;
  const selectedColPathIndex = selectedCell ? cols.findIndex((col) => col.id === selectedCell.colId) : -1;
  const mastersReady = !!inputMasterId && !!outputMasterId;

  const globalGainDb = masterGainDb;
  const canEditGlobalGain = !locked && mastersReady;

  const handleTransientMuteAllToggle = () => {
    if (!mastersReady || !hasAnyActiveRoute) return;
    setTransientMuteAll((prev) => !prev);
  };

  const inputModeLabel = inputDeviceMode === "loopback"
    ? "🔊"
    : inputDeviceMode === "both"
      ? "⥮"
      : "🎤";

  const handleRootClick = (event) => {
    if (event.target.closest(".matrix-grid")) return;
    if (event.target.closest(".route-indicator-btn")) return;
    setSelectedCell(null);
  };

  return (
    <div className={`studio-root${locked ? " is-locked" : ""}`} onClick={handleRootClick}>
      <header className="topbar rack-panel">
        <div className="brand-block">
          <span className="brand-grid-icon" aria-hidden="true">
            <span />
            <span />
            <span />
            <span />
            <span />
            <span />
            <span />
            <span />
            <span />
          </span>
          <div>
            <div className="brand-title-row">
              <h1>Audio Matrix Patch</h1>
              <span className="brand-version-pill">{APP_VERSION}</span>
            </div>
            <p>{contextState === "running" ? `Running${latencyLabel !== "n/a" ? ` · ${latencyLabel}` : ""}` : "Standby"}</p>
          </div>
        </div>

        <div className={`ui-controls-wrap ${controlsCollapsed ? "" : "is-open"}`} ref={quickControlsRef} aria-label={`Quick settings (${runtime})`}>
          <div className={`ui-quick-controls${controlsCollapsed ? " is-collapsed" : ""}`} aria-label="Quick style controls">
            <div className="quick-control-item">
              <button type="button" className="icon-btn icon-btn--square" title="Select background theme" aria-label="Select background theme" onClick={(event) => cyclePicker("background", event)}>⬛</button>
            </div>
            <div className="quick-control-item">
              <button type="button" className="icon-btn icon-btn--square icon-btn--theme-dot" title="Select accent color" aria-label="Select accent color" onClick={(event) => cyclePicker("accent", event)} style={{ "--dot-color": ACCENT_PRESETS[accentIndex]?.accentHl }} />
            </div>
            <div className="quick-control-item">
              <button type="button" className="icon-btn icon-btn--square" title="Select font" aria-label="Select font" onClick={(event) => cyclePicker("font", event)}>{FONT_PRESETS[fontIndex]?.label || "P"}</button>
            </div>
            <div className="quick-control-item">
              <button type="button" className="icon-btn icon-btn--square" title="Select font size" aria-label="Select font size" onClick={(event) => cyclePicker("fontSize", event)}>{FONT_SIZE_PRESETS[fontSizeIndex]?.label || "3"}</button>
            </div>
            <div className="quick-control-item">
              <button type="button" className="icon-btn icon-btn--square" title="Select UI scale" aria-label="Select UI scale" onClick={(event) => cyclePicker("uiScale", event)}>{UI_SCALE_PRESETS[uiScaleIndex]?.label || "MD"}</button>
            </div>
            <div className="quick-control-item">
              <button
                type="button"
                className={`icon-btn icon-btn--square ${startupAtBoot ? "is-on" : ""}`}
                title={startupAtBoot ? "Startup at boot: enabled" : "Startup at boot: disabled"}
                aria-label="Startup at boot options"
                onClick={(event) => cyclePicker("startup", event)}
              >
                ⏻
              </button>
            </div>
            <div className={`quick-control-picker${activeQuickPicker ? "" : " is-collapsed"}`} aria-live="polite">
              {quickPickerOptions.map((option) => (
                <button
                  key={option.key}
                  type="button"
                  className={`quick-picker-option${activeQuickKey === option.key ? " is-active" : ""}`}
                  onClick={(event) => {
                    event.stopPropagation();
                    const isSame = activeQuickKey === option.key;
                    applyQuickSelection(activeQuickPicker, option.key);
                    if (isSame) {
                      setActiveQuickPicker("");
                      setControlsCollapsed(true);
                    }
                  }}
                >
                  <span className="qp-swatch" style={{ background: option.swatch || option.accent || "transparent", color: option.accentHl || option.text || "var(--text)" }}>{option.label || ""}</span>
                  <span className="qp-label">{option.key}</span>
                </button>
              ))}
            </div>
          </div>
          <button
            type="button"
            className={`icon-btn icon-btn--square ui-controls-toggle ${controlsCollapsed ? "" : "active"}`}
            title={controlsCollapsed ? "Show style controls" : "Hide style controls"}
            aria-label={controlsCollapsed ? "Show style controls" : "Hide style controls"}
            aria-expanded={!controlsCollapsed}
            onClick={(event) => {
              event.stopPropagation();
              setControlsCollapsed((prev) => !prev);
              if (!controlsCollapsed) setActiveQuickPicker("");
            }}
          >
            <span className="ui-controls-toggle-icon" aria-hidden="true">{controlsCollapsed ? "⚙" : "×"}</span>
          </button>
        </div>

      </header>

      {error ? <div className="error-banner">{error}</div> : null}

      <main className="main-layout">
        <section
          className="matrix-wrap rack-panel"
          ref={matrixWrapRef}
          onPointerDown={(event) => {
            // Drag-scroll can start from nearly anywhere in the board, including tiles.
            if (event.button !== 0) return;
            const target = event.target;
            if (target.closest && target.closest(".matrix-corner, .corner-controls, .resize-handle, .row-head, .col-head, input, a")) {
              return;
            }
            const wrap = matrixWrapRef.current;
            if (!wrap) return;
            dragScrollRef.current = {
              tracking: true,
              dragging: false,
              startX: event.clientX,
              startY: event.clientY,
              scrollLeft: wrap.scrollLeft,
              scrollTop: wrap.scrollTop,
              pointerId: event.pointerId,
              blockNextClick: false,
            };
          }}
          onPointerMove={(event) => {
            const s = dragScrollRef.current;
            if (!s.tracking) return;
            const wrap = matrixWrapRef.current;
            if (!wrap) return;
            const dx = event.clientX - s.startX;
            const dy = event.clientY - s.startY;
            if (!s.dragging && Math.abs(dx) + Math.abs(dy) > 12) {
              s.dragging = true;
              try { wrap.setPointerCapture(event.pointerId); } catch (_) {}
              wrap.classList.add("is-drag-scrolling");
            }
            if (!s.dragging) return;
            const wheelState = matrixWheelScrollRef.current;
            if (wheelState.rafId) {
              cancelAnimationFrame(wheelState.rafId);
              wheelState.rafId = 0;
            }
            wrap.scrollLeft = s.scrollLeft - dx;
            wrap.scrollTop = s.scrollTop - dy;
            wheelState.targetLeft = wrap.scrollLeft;
            wheelState.targetTop = wrap.scrollTop;
          }}
          onPointerUp={(event) => {
            const s = dragScrollRef.current;
            if (!s.tracking) return;
            const wrap = matrixWrapRef.current;
            const hadDragged = s.dragging;
            s.tracking = false;
            s.dragging = false;
            s.blockNextClick = hadDragged;
            try { wrap?.releasePointerCapture(event.pointerId); } catch (_) {}
            wrap?.classList.remove("is-drag-scrolling");
          }}
          onPointerCancel={() => {
            const s = dragScrollRef.current;
            s.tracking = false;
            s.dragging = false;
            s.blockNextClick = false;
            matrixWrapRef.current?.classList.remove("is-drag-scrolling");
          }}
          onPointerLeave={() => {
            // Discard hover selection as soon as the pointer leaves the matrix surface.
            setShowResizeGuides(false);
            if (locked) return;
            setSelectedCell(null);
          }}
        >
          <div
            className={`matrix-grid${selectedCell ? " has-selection" : ""}${showResizeGuides ? " show-resize-guides" : ""}`}
            style={{
              gridTemplateColumns: `${scaledSourceWidth}px repeat(${matrixColumnCount}, ${scaledCellSize}px)`,
              gridTemplateRows: `${scaledDestinationHeight}px`,
              gridAutoRows: `${scaledCellSize}px`,
            }}
            onMouseLeave={() => {
              // Clear hover selection the moment the pointer exits the grid (not just the wrap).
              if (!locked) setSelectedCell(null);
              setShowResizeGuides(false);
            }}
            onMouseMove={(event) => {
              const rect = event.currentTarget.getBoundingClientRect();
              const x = event.clientX - rect.left;
              const y = event.clientY - rect.top;
              const threshold = 8;
              const nearSourceAxis = Math.abs(x - scaledSourceWidth) <= threshold;
              const nearDestAxis = Math.abs(y - scaledDestinationHeight) <= threshold;
              const nextVisible = nearSourceAxis || nearDestAxis;
              const tileAreaWidth = matrixColumnCount * scaledCellSize + Math.max(0, matrixColumnCount - 1) * GRID_GAP_SIZE;
              const tileAreaHeight = matrixRowCount * scaledCellSize + Math.max(0, matrixRowCount - 1) * GRID_GAP_SIZE;
              const insideTileBounds = x >= scaledSourceWidth
                && y >= scaledDestinationHeight
                && x <= (scaledSourceWidth + tileAreaWidth)
                && y <= (scaledDestinationHeight + tileAreaHeight);
              if (nextVisible !== showResizeGuides) {
                setShowResizeGuides(nextVisible);
              }

              if (!locked) {
                const tileWrap = event.target.closest(".tile-cell-wrap[data-rowid][data-colid]");
                if (tileWrap) {
                  const rowId = tileWrap.getAttribute("data-rowid");
                  const colId = tileWrap.getAttribute("data-colid");
                  if (rowId && colId && (selectedCell?.rowId !== rowId || selectedCell?.colId !== colId)) {
                    setSelectedCell({ rowId, colId });
                  }
                } else if (insideTileBounds) {
                  // Keep nearest tile selected even when hovering a grid gap.
                  const candidates = event.currentTarget.querySelectorAll(".tile-cell-wrap[data-rowid][data-colid]");
                  let nearest = null;
                  let nearestDistSq = Number.POSITIVE_INFINITY;
                  candidates.forEach((candidate) => {
                    const r = candidate.getBoundingClientRect();
                    const cx = r.left + (r.width / 2);
                    const cy = r.top + (r.height / 2);
                    const dx = event.clientX - cx;
                    const dy = event.clientY - cy;
                    const distSq = dx * dx + dy * dy;
                    if (distSq < nearestDistSq) {
                      nearestDistSq = distSq;
                      nearest = candidate;
                    }
                  });

                  if (nearest) {
                    const rowId = nearest.getAttribute("data-rowid");
                    const colId = nearest.getAttribute("data-colid");
                    if (rowId && colId && (selectedCell?.rowId !== rowId || selectedCell?.colId !== colId)) {
                      setSelectedCell({ rowId, colId });
                    }
                  }
                } else if (selectedCell) {
                  setSelectedCell(null);
                }
              }
            }}
          >
            <div className="matrix-corner matrix-corner-content">
              <div className="corner-controls" role="group" aria-label="Matrix quick controls">
                <button
                  type="button"
                  className={`corner-control-btn corner-control-tl ${powerOn ? "active" : ""}`}
                  onClick={togglePowerState}
                  title={powerOn ? "Power on (click to power off)" : "Power off (click to power on)"}
                  aria-label="Toggle power"
                >
                  <span aria-hidden="true">⏻</span>
                </button>
                <button
                  type="button"
                  className={`corner-control-btn corner-control-tr ${locked ? "active" : ""}`}
                  onClick={handleToggleLock}
                  title={locked ? "Unlock matrix" : "Lock matrix"}
                  aria-label={locked ? "Unlock matrix" : "Lock matrix"}
                >
                  <span aria-hidden="true">{locked ? "🔒" : "🔓"}</span>
                </button>
                <div className="buffer-control-wrap corner-control-buffer">
                  <button
                    type="button"
                    className={`corner-control-btn corner-control-btn--buffer ${isApplyingCaptureBuffer ? "active" : ""}`}
                    onClick={(event) => {
                      event.stopPropagation();
                      if (locked) return;
                      if (bufferDragSuppressClickRef.current) {
                        bufferDragSuppressClickRef.current = false;
                        return;
                      }
                      setCaptureBufferFromMenu(CAPTURE_BUFFER_DEFAULT);
                    }}
                    onPointerDown={(event) => {
                      if (locked) return;
                      event.currentTarget.setPointerCapture(event.pointerId);
                      bufferDragRef.current = {
                        startY: event.clientY,
                        startMs: captureBufferMs,
                      };
                      bufferDragSuppressClickRef.current = false;
                    }}
                    onPointerMove={(event) => {
                      if (!bufferDragRef.current || locked) return;
                      const deltaSteps = Math.trunc((bufferDragRef.current.startY - event.clientY) / 10);
                      const next = clamp(bufferDragRef.current.startMs + deltaSteps * 5, CAPTURE_BUFFER_MIN, CAPTURE_BUFFER_MAX);
                      if (next !== captureBufferMs) {
                        applyBufferDelta(next - captureBufferMs);
                        bufferDragSuppressClickRef.current = true;
                      }
                    }}
                    onPointerUp={() => {
                      bufferDragRef.current = null;
                    }}
                    onPointerCancel={() => {
                      bufferDragRef.current = null;
                      bufferDragSuppressClickRef.current = false;
                    }}
                    onWheel={(event) => {
                      if (locked) return;
                      event.preventDefault();
                      event.stopPropagation();
                      applyBufferDelta(event.deltaY < 0 ? 5 : -5);
                    }}
                    title={`${isApplyingCaptureBuffer ? "Applying" : "Capture buffer"} ${captureBufferMs}ms. Drag/wheel for 5ms steps, click to reset.`}
                    aria-label="Adjust capture buffer"
                    disabled={locked}
                  >
                    <span className="buffer-readout" aria-hidden="true">
                      <span>{`${captureBufferMs}`}</span>
                      <span className="buffer-unit">ms</span>
                    </span>
                  </button>
                </div>
                <div
                  className="corner-control-mid corner-gain-wheel"
                  onClick={(e) => {
                    e.stopPropagation();
                    if (!canEditGlobalGain) return;
                    if (wheelDragSuppressClickRef.current) {
                      wheelDragSuppressClickRef.current = false;
                      return;
                    }
                    setMasterGainDb(0);
                  }}
                  onPointerDown={(e) => {
                    if (!canEditGlobalGain) return;
                    e.currentTarget.setPointerCapture(e.pointerId);
                    wheelDragRef.current = {
                      startY: e.clientY,
                      startDb: globalGainDb,
                      startOffsetPx: wheelVisualOffsetPx,
                    };
                    wheelDragSuppressClickRef.current = false;
                  }}
                  onPointerMove={(e) => {
                    if (!wheelDragRef.current) return;
                    const dragPx = e.clientY - wheelDragRef.current.startY;
                    setWheelVisualOffsetPx(wheelDragRef.current.startOffsetPx + dragPx);
                    const deltaDb = -(dragPx / GLOBAL_GAIN_DRAG_PX_PER_STEP) * 0.5;
                    const next = clamp(Math.round((wheelDragRef.current.startDb + deltaDb) * 2) / 2, DB_MIN, DB_MAX);
                    if (Math.abs(next - masterGainDbRef.current) >= 0.49) {
                      masterGainDbRef.current = next;
                      setMasterGainDb(next);
                      wheelDragSuppressClickRef.current = true;
                    }
                  }}
                  onWheel={(e) => {
                    if (!canEditGlobalGain) return;
                    e.preventDefault();
                    e.stopPropagation();
                    const primary = Math.abs(e.deltaX) > Math.abs(e.deltaY) ? e.deltaX : e.deltaY;
                    applyGlobalGainDelta(primary < 0 ? 0.5 : -0.5);
                  }}
                  onPointerUp={finishWheelDrag}
                  onPointerCancel={finishWheelDrag}
                  style={{ cursor: canEditGlobalGain ? "ns-resize" : "default", opacity: canEditGlobalGain ? 1 : 0.45 }}
                  title={canEditGlobalGain ? "Master gain: drag up/down or scroll wheel" : "Unlock matrix to edit master gain"}
                  role="spinbutton"
                  aria-valuenow={globalGainDb}
                  aria-valuemin={DB_MIN}
                  aria-valuemax={DB_MAX}
                  aria-label="Global gain"
                >
                  <div className="corner-gain-wheel-drum" aria-hidden="true" style={{ backgroundPositionY: `${wheelVisualOffsetPx}px` }} />
                  <span className="corner-gain-wheel-value">
                    {globalGainDb > 0 ? `+${globalGainDb.toFixed(1)}` : globalGainDb.toFixed(1)}
                    <span className="corner-gain-wheel-unit">dB</span>
                  </span>
                </div>
                <button
                  type="button"
                  className={`corner-control-btn corner-control-global-mute ${transientMuteAll ? "active muted" : ""}`}
                  onClick={handleTransientMuteAllToggle}
                  disabled={!hasAnyActiveRoute || !mastersReady}
                  title={
                    !mastersReady
                      ? "Select input and output masters first"
                      : (transientMuteAll ? "Global mute on (click to unmute)" : "Global mute off (click to mute all)")
                  }
                  aria-label="Toggle global mute"
                >
                  <span aria-hidden="true">🔈︎</span>
                </button>
                <button
                  type="button"
                  className={`corner-control-btn corner-control-br ${viewMode === "channel" ? "active" : ""}`}
                  aria-label="Toggle channel view"
                  title={viewMode === "channel" ? "Switch to Device View" : "Switch to Channel View"}
                  onClick={handleToggleViewMode}
                  disabled={locked}
                >
                  <span aria-hidden="true">⌗</span>
                </button>
                <button
                  type="button"
                  className={`corner-control-btn corner-control-bl ${showAllDevices ? "active" : ""}`}
                  onClick={() => {
                    setShowAllDevices((prev) => !prev);
                  }}
                  title={showAllDevices ? "Hide unconfigured devices" : "Show all discovered devices"}
                  aria-label="Toggle all device visibility"
                  disabled={locked}
                >
                  <span aria-hidden="true">≣</span>
                </button>
                <button
                  type="button"
                  className={`corner-control-btn corner-control-reload ${isReloadingDevices ? "active" : ""}`}
                  onClick={handleReloadDevices}
                  disabled={isReloadingDevices || locked}
                  title="Restart and reload devices"
                  aria-label="Restart and reload devices"
                >
                  <span aria-hidden="true">↻</span>
                </button>
                <button
                  type="button"
                  className={`corner-control-btn corner-control-input-mode ${inputDeviceMode === "both" ? "active" : ""}`}
                  onClick={handleToggleInputDeviceMode}
                  disabled={!hasNativeBridge || locked}
                  title={`Input device list mode: ${inputDeviceMode} (click to cycle)`}
                  aria-label={`Input device list mode: ${inputDeviceMode}`}
                >
                  <span aria-hidden="true">{inputModeLabel}</span>
                </button>
              </div>
            </div>
            <button
              type="button"
              className="resize-handle grid-resize-handle grid-source-width-handle"
              style={{ left: `${scaledSourceWidth + 1}px` }}
              onMouseDown={beginResizeSourceWidth}
              aria-label="Resize source label width"
            />
            <button
              type="button"
              className="resize-handle grid-resize-handle grid-dest-height-handle"
              style={{ top: `${scaledDestinationHeight + 1}px` }}
              onMouseDown={beginResizeDestinationHeight}
              aria-label="Resize destination label height"
            />

            {cols.map((col) => {
              if (viewMode === "channel" && !col.isChannelStart) return null;

              const channelCount = Math.max(1, col.channelCount || 1);
              const isActive = activeRoutes.has(col.id) || (col.pairedIds || []).some((pid) => activeRoutes.has(pid));
              const span = channelCount;
              const colChannelLevels = getColChannelLevels(col);

              const isColSelected = selectedCell?.colId === col.id || (col.pairedIds || []).some((pid) => selectedCell?.colId === pid);
              return (
                <div
                  key={col.id}
                  className={[
                    "col-head",
                    "sort-handle",
                    !isActive && "inactive",
                    isColSelected && "active-axis",
                    col.isMaster && "master-axis",

                  ].filter(Boolean).join(" ")}
                  title={`${col.fullLabel || col.label}\nDouble-click to set output master`}
                  style={span > 1 ? { gridColumn: `span ${span}` } : undefined}
                  draggable={!locked}
                  onDragStart={beginSortDrag("destination", col.outputDeviceId)}
                  onDragEnd={endSortDrag("destination")}
                  onDragOver={overSortTarget("destination", col.outputDeviceId)}
                  onDrop={dropSortTarget("destination")}
                >
                  <div
                    className="col-main"
                    onDoubleClick={() => handleOutputLabelDoubleClick(col.id)}
                  >
                    {col.isMaster ? <span className="master-badge master-badge-col">MASTER</span> : null}
                    <div className="card-meter-bg card-meter-bg-col-split" aria-hidden="true" style={{ gridTemplateColumns: `repeat(${channelCount}, 1fr)` }}>
                      {colChannelLevels.map((lvl, i) => (
                        <span
                          key={i}
                          className={`meter-bar meter-bar-${i === 0 ? "l" : i === 1 ? "r" : `${i}`}`}
                          style={{ height: `${Math.round((lvl || 0) * 100)}%` }}
                        />
                      ))}
                    </div>
                    {col.label ? (
                      <div className="v-label">
                        <span className="label-main">{col.label}</span>
                        {col.hardwareLabel ? <span className="label-sub">{col.hardwareLabel}</span> : null}
                      </div>
                    ) : (
                      <div className="v-label-spacer" />
                    )}
                  </div>
                  <span className="col-channels-box" aria-hidden="true" style={{ gridTemplateColumns: `repeat(${channelCount}, 1fr)` }}>
                    {Array.from({ length: channelCount }, (_, i) => (
                      <span key={i} className="axis-split-label axis-split-cell">
                        {getChannelAxisLabel(channelCount, i)}
                      </span>
                    ))}
                  </span>
                </div>
              );
            })}

            {rows.map((row, rowIndex) => {
              const channelCount = Math.max(1, row.channelCount || 1);
              const isRowActive = activeRoutes.has(row.id) || (row.pairedIds || []).some((pid) => activeRoutes.has(pid));
              const isRowAxisActive = selectedCell?.rowId === row.id || (row.pairedIds || []).some((pid) => selectedCell?.rowId === pid);
              const skipHead = viewMode === "channel" && !row.isChannelStart;
              const rowChannelLevels = getRowChannelLevels(row);
              const rowSpan = viewMode === "device" ? channelCount : 1;
              return (
              <React.Fragment key={row.id}>
                {!skipHead && (
                <div
                  className={[
                    "row-head",
                    "sort-handle",
                    !isRowActive && "inactive",
                    isRowAxisActive && "active-axis",
                    row.isMaster && "master-axis",

                  ].filter(Boolean).join(" ")}
                  title={`${row.fullLabel || row.label}\nDouble-click to set input master`}
                  style={channelCount > 1 ? { gridRow: `span ${channelCount}` } : undefined}
                  draggable={!locked}
                  onDragStart={beginSortDrag("source", row.deviceId)}
                  onDragEnd={endSortDrag("source")}
                  onDragOver={overSortTarget("source", row.deviceId)}
                  onDrop={dropSortTarget("source")}
                >
                  <div
                    className="row-main"
                    onDoubleClick={() => handleInputLabelDoubleClick(row.id)}
                  >
                    {row.isMaster ? <span className="master-badge master-badge-row">MASTER</span> : null}
                    <div className="card-meter-bg card-meter-bg-row-split" aria-hidden="true" style={{ gridTemplateRows: `repeat(${channelCount}, 1fr)` }}>
                      {rowChannelLevels.map((lvl, i) => (
                        <span
                          key={i}
                          className={`meter-bar meter-bar-${i === 0 ? "l" : i === 1 ? "r" : `${i}`}`}
                          style={{ width: `${Math.round((lvl || 0) * 100)}%` }}
                        />
                      ))}
                    </div>
                    {row.label ? (
                      <div className="row-label-btn">
                        <span className="label-main">{row.label}</span>
                        {row.hardwareLabel ? <span className="label-sub">{row.hardwareLabel}</span> : null}
                      </div>
                    ) : (
                      <div className="row-label-spacer" />
                    )}
                  </div>
                  <span className="row-channels-box" aria-hidden="true" style={{ gridTemplateRows: `repeat(${channelCount}, 1fr)` }}>
                    {Array.from({ length: channelCount }, (_, i) => (
                      <span key={i} className="axis-split-label axis-split-cell">
                        {getChannelAxisLabel(channelCount, i)}
                      </span>
                    ))}
                  </span>
                </div>
                )}

                {cols.map((col, colIndex) => {
                  const key = getCellKey(row.id, col.id);
                  const state = activeMatrix[key] || makeDefaultConnection();
                  const selected = selectedCell?.rowId === row.id && selectedCell?.colId === col.id;
                  const pathLeft = selectedRowPathIndex >= 0 && selectedColPathIndex >= 0
                    && rowIndex === selectedRowPathIndex && colIndex < selectedColPathIndex;
                  const pathUp = selectedRowPathIndex >= 0 && selectedColPathIndex >= 0
                    && colIndex === selectedColPathIndex && rowIndex < selectedRowPathIndex;
                  const blockedByLoopbackSelfRoute = isLoopbackSelfRoute(row.deviceId, col.outputDeviceId);
                  const colSpan = viewMode === "device" ? Math.max(1, col.channelCount || 1) : 1;
                  const tileWidth = scaledCellSize * colSpan + GRID_GAP_SIZE * (colSpan - 1);
                  const tileHeight = scaledCellSize * rowSpan + GRID_GAP_SIZE * (rowSpan - 1);
                  return (
                      <div
                        key={key}
                        className={`tile-cell-wrap${blockedByLoopbackSelfRoute ? " blocked" : ""}`}
                        {...(!blockedByLoopbackSelfRoute ? { "data-rowid": row.id } : {})}
                        {...(!blockedByLoopbackSelfRoute ? { "data-colid": col.id } : {})}
                        style={{
                          width: `${tileWidth}px`,
                          height: `${tileHeight}px`,
                          ...(rowSpan > 1 ? { gridRow: `span ${rowSpan}` } : {}),
                          ...(colSpan > 1 ? { gridColumn: `span ${colSpan}` } : {}),
                        }}
                      >
                      {blockedByLoopbackSelfRoute ? (
                        <div
                          className={[
                            "cell",
                            "blocked",
                            pathLeft && "path-left",
                            pathUp && "path-up",
                          ].filter(Boolean).join(" ")}
                          style={{ width: `${tileWidth}px`, height: `${tileHeight}px` }}
                          title="Loopback input cannot route to the same physical output device"
                        />
                      ) : (
                      <button
                        type="button"
                        className={[
                          "cell",
                          state.on ? "on" : "off",
                          selected && "selected",
                          state.phaseInverted && "phase-inverted",
                          pathLeft && "path-left",
                          pathUp && "path-up",
                        ].filter(Boolean).join(" ")}
                        style={{ width: `${tileWidth}px`, height: `${tileHeight}px` }}
                        onMouseEnter={() => {
                          if (locked) return;
                          if (dragScrollRef.current.dragging) return;
                          if (selectedCell?.rowId === row.id && selectedCell?.colId === col.id) return;
                          setSelectedCell({ rowId: row.id, colId: col.id });
                        }}
                        onClick={() => {
                          if (dragScrollRef.current.blockNextClick) {
                            dragScrollRef.current.blockNextClick = false;
                            return;
                          }
                          updateConnection(row.id, col.id, {
                            ...state,
                            on: !state.on,
                          });
                        }}
                        onWheel={(event) => {
                          if (locked) return;
                          if (!state.on) return;
                          event.preventDefault();
                          event.stopPropagation();
                          adjustGainForCell(row.id, col.id, event.deltaY < 0 ? 0.5 : -0.5);
                        }}
                        onContextMenu={(event) => {
                          if (locked) return;
                          event.preventDefault();
                          event.stopPropagation();
                          setSelectedCell({ rowId: row.id, colId: col.id });
                          updateConnection(row.id, col.id, (prev) => ({
                            ...prev,
                            on: true,
                            phaseInverted: !prev.phaseInverted,
                          }));
                        }}
                        title={`${row.label} -> ${col.label}`}
                      >
                        {Math.abs(state.gainDb || 0) >= 0.5 ? (
                          <span className="tile-gain-readout">{`${state.gainDb > 0 ? "+" : ""}${(Number(state.gainDb) || 0).toFixed(1)}dB`}</span>
                        ) : null}
                      </button>
                      )}
                    </div>
                  );
                })}
              </React.Fragment>
              );
            })}
          </div>

        </section>
      </main>

      <div className="inline-editor docked rack-panel">
        <div className="dock-col dock-card">
          <div className="dock-card-main-wrap">
            <div className="card-main-copy card-main-copy-split">
              <div className="card-meter-bg card-meter-bg-row-split" aria-hidden="true" style={{ gridTemplateRows: `repeat(${selectedSourceChannelLabels.length}, 1fr)` }}>
                {selectedSourceChannelLabels.map((_, i) => (
                  <span key={i} className={`meter-bar meter-bar-${i === 0 ? "l" : "r"}`} style={{ width: `${Math.round((selectedSourceSplit[i] ?? 0) * 100)}%` }} />
                ))}
              </div>
              {selectedSourceDeviceId && selectedSourceDeviceId === inputMasterId ? <span className="detail-master-badge detail-master-badge-vert">MASTER</span> : null}
              <div className="detail-name-stack">
                <span className="detail-name-main">{selectedSource?.label || "Source"}</span>
                {selectedSource?.hardwareLabel ? <span className="detail-name-sub">{selectedSource.hardwareLabel}</span> : null}
              </div>
            </div>
            <span className="row-channels-box detail-channels-vert detail-channels-outside" aria-hidden="true" style={{ gridTemplateRows: `repeat(${selectedSourceChannelLabels.length}, 1fr)` }}>
              {selectedSourceChannelLabels.map((label, i) => (
                <span key={`src-${i}`} className="axis-split-label axis-split-cell">{label}</span>
              ))}
            </span>
          </div>
          <div className="card-metrics-box">
            <div className="metric-tile">
              <span className="metric-title">Latency</span>
              <span className="metric-value">{sourceLatencyLabel}</span>
            </div>
            <div className="metric-tile">
              <span className="metric-title">Jitter</span>
              <span className="metric-value">{jitterLabel}</span>
            </div>
          </div>
        </div>

        <div className="dock-col dock-center">
          <div className="dock-center-stack">
            <button
              type="button"
              className={`route-indicator-btn ${routeIndicatorActive ? "active" : "inactive"}`}
              title={routeIndicatorActive ? "Selected route is active" : "Selected route is disabled"}
              aria-label={routeIndicatorActive ? "Selected route active" : "Selected route disabled"}
            >
              <span aria-hidden="true">{routeIndicatorIcon}</span>
            </button>
          </div>
        </div>

        <div className="dock-col dock-card">
          <div className="card-metrics-box">
            <div className="metric-tile">
              <span className="metric-title">Latency</span>
              <span className="metric-value">{destinationLatencyLabel}</span>
            </div>
            <div className="metric-tile">
              <span className="metric-title">Jitter</span>
              <span className="metric-value">{jitterLabel}</span>
            </div>
          </div>
          <div className="dock-card-main-wrap">
            <div className="card-main-copy card-main-copy-split">
              <div className="card-meter-bg card-meter-bg-row-split" aria-hidden="true" style={{ gridTemplateRows: `repeat(${selectedDestinationChannelLabels.length}, 1fr)` }}>
                {selectedDestinationChannelLabels.map((_, i) => (
                  <span key={i} className={`meter-bar meter-bar-${i === 0 ? "l" : "r"}`} style={{ width: `${Math.round((selectedDestinationSplit[i] ?? 0) * 100)}%` }} />
                ))}
              </div>
              {selectedDestinationDeviceId && selectedDestinationDeviceId === outputMasterId ? <span className="detail-master-badge detail-master-badge-vert">MASTER</span> : null}
              <div className="detail-name-stack">
                <span className="detail-name-main">{selectedDestination?.label || "Destination"}</span>
                {selectedDestination?.hardwareLabel ? <span className="detail-name-sub">{selectedDestination.hardwareLabel}</span> : null}
              </div>
            </div>
            <span className="row-channels-box detail-channels-vert detail-channels-outside" aria-hidden="true" style={{ gridTemplateRows: `repeat(${selectedDestinationChannelLabels.length}, 1fr)` }}>
              {selectedDestinationChannelLabels.map((label, i) => (
                <span key={`dst-${i}`} className="axis-split-label axis-split-cell">{label}</span>
              ))}
            </span>
          </div>
        </div>
      </div>
    </div>
  );
}
