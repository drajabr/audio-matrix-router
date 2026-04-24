import React, { useEffect, useMemo, useRef, useState } from "react";
import { Volume2, VolumeX } from "lucide-react";

const APP_VERSION = __APP_VERSION__;
const STORAGE_KEY = "audio-router-matrix-v3";
const DB_MIN = -60;
const DB_MAX = 12;
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
const QUICK_CONTROLS_COLLAPSED_KEY = "amrQuickControlsCollapsed";
const POWER_ON_KEY = "amrPowerOn";

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
];

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
        const splitter = ctx.createChannelSplitter(2);
        source.connect(splitter);

        const deviceAnalyzer = ctx.createAnalyser();
        deviceAnalyzer.fftSize = 512;
        source.connect(deviceAnalyzer);
        this.inputMeterAnalyzers.set(`dev:${input.deviceId}`, deviceAnalyzer);

        for (let ch = 0; ch < 2; ch += 1) {
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
      const destination = ctx.createMediaStreamDestination();
      const monitorAnalyzer = ctx.createAnalyser();
      monitorAnalyzer.fftSize = 512;

      const postGain = ctx.createGain();
      postGain.gain.value = 1;

      let node;
      if (viewMode === "channel") {
        node = ctx.createChannelMerger(2);
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
        const outSplitter = ctx.createChannelSplitter(2);
        postGain.connect(outSplitter);
        const ana0 = ctx.createAnalyser();
        ana0.fftSize = 512;
        const ana1 = ctx.createAnalyser();
        ana1.fftSize = 512;
        outSplitter.connect(ana0, 0);
        outSplitter.connect(ana1, 1);
        this.outputMeterAnalyzers.set(`ch:${output.deviceId}:0`, ana0);
        this.outputMeterAnalyzers.set(`ch:${output.deviceId}:1`, ana1);
        this.outputMeterSplitters.set(output.deviceId, outSplitter);
      } else {
        this.outputMeterAnalyzers.set(`ch:${output.deviceId}:0`, monitorAnalyzer);
        this.outputMeterAnalyzers.set(`ch:${output.deviceId}:1`, monitorAnalyzer);
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

        for (let inCh = 0; inCh < 2; inCh += 1) {
          for (const output of outputs) {
            const outputNode = this.outputNodes.get(output.deviceId);
            if (!outputNode) continue;

            for (let outCh = 0; outCh < 2; outCh += 1) {
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
  const [bufferMs, setBufferMs] = useState(null);
  const [jitterMs, setJitterMs] = useState(null);
  const [clockKhz, setClockKhz] = useState(null);
  const [error, setError] = useState("");
  const [isReloadingDevices, setIsReloadingDevices] = useState(false);
  const [backgroundIndex, setBackgroundIndex] = useState(() => getStoredIndex(BACKGROUND_KEY, BACKGROUND_PRESETS, 0));
  const [accentIndex, setAccentIndex] = useState(() => getStoredIndex(ACCENT_KEY, ACCENT_PRESETS, Math.max(0, ACCENT_PRESETS.findIndex((p) => p.key === "white"))));
  const [fontIndex, setFontIndex] = useState(() => getStoredIndex(FONT_KEY, FONT_PRESETS, Math.max(0, FONT_PRESETS.findIndex((p) => p.key === "consolas"))));
  const [fontSizeIndex, setFontSizeIndex] = useState(() => getStoredIndex(FONT_SIZE_KEY, FONT_SIZE_PRESETS, 4));
  const [controlsCollapsed, setControlsCollapsed] = useState(() => {
    if (typeof window === "undefined") return true;
    return localStorage.getItem(QUICK_CONTROLS_COLLAPSED_KEY) !== "0";
  });
  const [activeQuickPicker, setActiveQuickPicker] = useState("");
  const [startupAtBoot, setStartupAtBoot] = useState(false);
  const [showAllDevices, setShowAllDevices] = useState(false);
  const [locked, setLocked] = useState(false);
  const [powerOn, setPowerOn] = useState(() => {
    if (typeof window === "undefined") return true;
    return localStorage.getItem(POWER_ON_KEY) !== "0";
  });
  const [tileMenuCell, setTileMenuCell] = useState(null);
  const [gainAdjustCell, setGainAdjustCell] = useState(null);
  const [transientMuteAll, setTransientMuteAll] = useState(false);

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
  const latencyLastRef = useRef(null);
  const latencyJitterRef = useRef(0);

  useEffect(() => {
    matrixRef.current = matrixByView;
  }, [matrixByView]);

  useEffect(() => {
    modeRef.current = viewMode;
  }, [viewMode]);

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

  const visibleInputs = showAllDevices ? inputs : routedInputs;
  const visibleOutputs = showAllDevices ? outputs : routedOutputs;

  const monitoredInputs = useMemo(() => {
    if (locked) return routedInputs;
    return showAllDevices ? inputs : routedInputs;
  }, [locked, showAllDevices, inputs, routedInputs]);

  const monitoredOutputs = useMemo(() => {
    if (locked) return routedOutputs;
    return showAllDevices ? outputs : routedOutputs;
  }, [locked, showAllDevices, outputs, routedOutputs]);

  useEffect(() => {
    const root = document.documentElement;
    const background = BACKGROUND_PRESETS[backgroundIndex] || BACKGROUND_PRESETS[0];
    const accent = ACCENT_PRESETS[accentIndex] || ACCENT_PRESETS[0];
    const font = FONT_PRESETS[fontIndex] || FONT_PRESETS[0];
    const fontSize = FONT_SIZE_PRESETS[fontSizeIndex] || FONT_SIZE_PRESETS[2];

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

    localStorage.setItem(BACKGROUND_KEY, background.key);
    localStorage.setItem(ACCENT_KEY, accent.key);
    localStorage.setItem(FONT_KEY, font.key);
    localStorage.setItem(FONT_SIZE_KEY, fontSize.key);

    if (devicesDiscoveredRef.current) {
      persistState(buildPersistedState({
        backgroundKey: background.key,
        accentKey: accent.key,
        fontKey: font.key,
        fontSizeKey: fontSize.key,
      }));
    }
  }, [backgroundIndex, accentIndex, fontIndex, fontSizeIndex]);

  useEffect(() => {
    localStorage.setItem(QUICK_CONTROLS_COLLAPSED_KEY, controlsCollapsed ? "1" : "0");
    if (devicesDiscoveredRef.current) {
      persistState(buildPersistedState({ controlsCollapsed }));
    }
  }, [controlsCollapsed]);

  useEffect(() => {
    localStorage.setItem(POWER_ON_KEY, powerOn ? "1" : "0");
    if (devicesDiscoveredRef.current) {
      persistState(buildPersistedState({ powerOn }));
    }
  }, [powerOn]);

  useEffect(() => {
    if (!devicesDiscoveredRef.current) return;
    persistState(buildPersistedState({ showAllDevices, locked }));
  }, [showAllDevices, locked]);

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
    setTileMenuCell(null);
    setGainAdjustCell(null);
  }, [locked]);

  useEffect(() => {
    const onClick = (event) => {
      if (!quickControlsRef.current?.contains(event.target)) {
        setActiveQuickPicker("");
        setControlsCollapsed(true);
      }
      if (!event.target.closest(".tile-context-menu")) {
        setTileMenuCell(null);
        setGainAdjustCell(null);
      }
    };

    const onKeyDown = (event) => {
      if (event.key === "Escape") {
        setActiveQuickPicker("");
        setControlsCollapsed(true);
        setTileMenuCell(null);
        setGainAdjustCell(null);
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
    const poll = () => {
      const ctx = managerRef.current.context;
      if (ctx && ctx.state === "running") {
        const base = (ctx.baseLatency || 0) * 1000;
        const out = (ctx.outputLatency || 0) * 1000;
        const lat = base + out;

        setLatencyMs(lat > 0 ? Math.round(lat) : null);
        setBufferMs(base > 0 ? Math.round(base * 10) / 10 : null);
        setClockKhz(ctx.sampleRate ? Math.round((ctx.sampleRate / 1000) * 10) / 10 : null);

        if (latencyLastRef.current != null) {
          const delta = Math.abs(lat - latencyLastRef.current);
          latencyJitterRef.current = latencyJitterRef.current === 0
            ? delta
            : latencyJitterRef.current * 0.82 + delta * 0.18;
          setJitterMs(Math.round(latencyJitterRef.current * 10) / 10);
        }
        latencyLastRef.current = lat;
      } else {
        setLatencyMs(null);
        setBufferMs(null);
        setJitterMs(null);
        setClockKhz(null);
        latencyLastRef.current = null;
        latencyJitterRef.current = 0;
      }
    };

    poll();
    const id = setInterval(poll, 250);
    return () => clearInterval(id);
  }, []);

  const rows = useMemo(() => {
    if (viewMode === "device") {
      return visibleInputs.map((input) => {
        const rawLabel = inputLabels[input.deviceId] || input.label;
        const parts = getDeviceLabelParts(rawLabel);
        return {
          id: `dev:${input.deviceId}`,
          label: parts.primary,
          hardwareLabel: parts.hardware,
          deviceRefLabel: parts.deviceRef,
          fullLabel: rawLabel,
          deviceId: input.deviceId,
          isMaster: input.deviceId === inputMasterId,
        };
      });
    }

    return visibleInputs.flatMap((input) => {
      const rawLabel = inputLabels[input.deviceId] || input.label;
      const parts = getDeviceLabelParts(rawLabel);
      return [
        {
          id: `ch:${input.deviceId}:0`,
          pairedId: `ch:${input.deviceId}:1`,
          label: parts.primary,
          hardwareLabel: parts.hardware,
          deviceRefLabel: parts.deviceRef,
          fullLabel: rawLabel,
          deviceId: input.deviceId,
          isChannelStart: true,
          isMaster: input.deviceId === inputMasterId,
        },
        {
          id: `ch:${input.deviceId}:1`,
          pairedId: `ch:${input.deviceId}:0`,
          label: parts.primary,
          hardwareLabel: parts.hardware,
          deviceRefLabel: parts.deviceRef,
          fullLabel: rawLabel,
          deviceId: input.deviceId,
          isChannelEnd: true,
          isMaster: input.deviceId === inputMasterId,
        },
      ];
    });
  }, [visibleInputs, inputLabels, viewMode, inputMasterId]);

  const cols = useMemo(() => {
    if (viewMode === "device") {
      return visibleOutputs.map((output) => {
        const rawLabel = outputLabels[output.deviceId] || output.label;
        const parts = getDeviceLabelParts(rawLabel);
        return {
          id: `dev:${output.deviceId}`,
          label: parts.primary,
          hardwareLabel: parts.hardware,
          deviceRefLabel: parts.deviceRef,
          fullLabel: rawLabel,
          outputDeviceId: output.deviceId,
          isMaster: output.deviceId === outputMasterId,
        };
      });
    }

    return visibleOutputs.flatMap((output) => {
      const rawLabel = outputLabels[output.deviceId] || output.label;
      const parts = getDeviceLabelParts(rawLabel);
      return [
        {
          id: `ch:${output.deviceId}:0`,
          pairedId: `ch:${output.deviceId}:1`,
          label: parts.primary,
          hardwareLabel: parts.hardware,
          deviceRefLabel: parts.deviceRef,
          fullLabel: rawLabel,
          outputDeviceId: output.deviceId,
          isChannelStart: true,
          isMaster: output.deviceId === outputMasterId,
        },
        {
          id: `ch:${output.deviceId}:1`,
          pairedId: `ch:${output.deviceId}:0`,
          label: parts.primary,
          hardwareLabel: parts.hardware,
          deviceRefLabel: parts.deviceRef,
          fullLabel: rawLabel,
          outputDeviceId: output.deviceId,
          isChannelEnd: true,
          isMaster: output.deviceId === outputMasterId,
        },
      ];
    });
  }, [visibleOutputs, outputLabels, viewMode, outputMasterId]);

  const cellSize = viewMode === "channel" ? CHANNEL_CELL_SIZE : DEVICE_CELL_SIZE;

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

    const state = await window.__nativeBridgeInvoke("getState", {});
    const nativeInputs = Array.isArray(state?.inputs) ? state.inputs : [];
    const nativeOutputs = Array.isArray(state?.outputs) ? state.outputs : [];

    const active = !!connection?.on && !connection?.muted;
    const baseGainDb = Number.isFinite(connection?.gainDb) ? connection.gainDb : 0;

    const routeCalls = [];

    if (mode === "channel") {
      const rowParsed = parseChannelId(rowId);
      const colParsed = parseChannelId(colId);
      if (!rowParsed || !colParsed) return;

      const inputDevice = nativeInputs.find((d) => d.deviceId === rowParsed.deviceId);
      const outputDevice = nativeOutputs.find((d) => d.deviceId === colParsed.deviceId);
      if (!inputDevice || !outputDevice) return;

      const inCh = (inputDevice.offset || 0) + rowParsed.channelIndex;
      const outCh = (outputDevice.offset || 0) + colParsed.channelIndex;

      routeCalls.push(window.__nativeBridgeInvoke("setCrosspoint", {
        inCh,
        outCh,
        active,
        gainDb: baseGainDb,
      }));
    } else {
      const inputDeviceId = rowId?.startsWith("dev:") ? rowId.slice(4) : "";
      const outputDeviceId = colId?.startsWith("dev:") ? colId.slice(4) : "";
      if (!inputDeviceId || !outputDeviceId) return;

      const inputDevice = nativeInputs.find((d) => d.deviceId === inputDeviceId);
      const outputDevice = nativeOutputs.find((d) => d.deviceId === outputDeviceId);
      if (!inputDevice || !outputDevice) return;

      const inChannels = Array.from({ length: inputDevice.channels || 0 }, (_, i) => i);
      const outChannels = Array.from({ length: outputDevice.channels || 0 }, (_, i) => i);
      const routes = buildDeviceToChannelRouteMatrix(inChannels, outChannels);

      routes.forEach((route) => {
        routeCalls.push(window.__nativeBridgeInvoke("setCrosspoint", {
          inCh: (inputDevice.offset || 0) + route.inChannel,
          outCh: (outputDevice.offset || 0) + route.outChannel,
          active,
          gainDb: clamp(baseGainDb + route.gainOffsetDb, DB_MIN, DB_MAX),
        }));
      });
    }

    if (routeCalls.length > 0) {
      await Promise.all(routeCalls);
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
      if (conn.on && !conn.muted) {
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
    if (inputMasterId && !inputIds.has(inputMasterId)) {
      setInputMasterId("");
    }
    if (outputMasterId && !outputIds.has(outputMasterId)) {
      setOutputMasterId("");
    }
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
    controlsCollapsed,
    showAllDevices,
    powerOn,
    locked,
    inputLabels,
    outputLabels,
    inputMasterId,
    outputMasterId,
    viewMode,
    labelSizing,
    matrixByView,
    inputOrder: inputs.map((d) => d.deviceId),
    outputOrder: outputs.map((d) => d.deviceId),
    ...overrides,
  });

  const persistState = (next) => {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(next));
    } catch (_) {
      // no-op
    }

    if (hasNativeBridge) {
      window.__nativeBridgeInvoke("setUiPreferences", { json: JSON.stringify(next) }).catch(() => {});
    }
  };

  const loadPersistedState = async () => {
    if (hasNativeBridge) {
      try {
        const raw = await window.__nativeBridgeInvoke("getUiPreferences", {});
        if (typeof raw === "string" && raw.trim()) {
          return JSON.parse(raw);
        }
      } catch (_) {
        // Fall through to local storage fallback.
      }
    }

    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      return raw ? JSON.parse(raw) : null;
    } catch (_) {
      return null;
    }
  };

  const resolveLinearGain = (connection) => {
    if (!connection.on || connection.muted || transientMuteAll) return 0;
    const direction = connection.phaseInverted ? -1 : 1;
    return dbToLinear(connection.gainDb) * direction;
  };

  const applyMatrixToEngine = (mode, matrix) => {
    Object.entries(matrix).forEach(([key, connection]) => {
      const [rowId, colId] = key.split("::");
      managerRef.current.setCrosspointGain(mode, rowId, colId, resolveLinearGain(connection));
    });
  };

  const rebuildAudioGraph = async (mode, nextMatrixByView = matrixRef.current) => {
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

  const discoverDevices = async () => {
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
      if (typeof saved?.controlsCollapsed === "boolean") setControlsCollapsed(saved.controlsCollapsed);
      if (typeof saved?.showAllDevices === "boolean") setShowAllDevices(saved.showAllDevices);
      if (typeof saved?.powerOn === "boolean") setPowerOn(saved.powerOn);
      if (!hasNativeBridge && typeof saved?.locked === "boolean") setLocked(saved.locked);

      await navigator.mediaDevices.getUserMedia({ audio: true, video: false });

      const devices = await navigator.mediaDevices.enumerateDevices();
      const discoveredInputs = devices
        .filter((d) => d.kind === "audioinput" && d.deviceId !== "default" && d.deviceId !== "communications")
        .map((d, i) => ({
          deviceId: d.deviceId,
          label: d.label || `Input ${i + 1}`,
        }));

      const discoveredOutputs = devices
        .filter((d) => d.kind === "audiooutput" && d.deviceId !== "default" && d.deviceId !== "communications")
        .map((d, i) => ({
          deviceId: d.deviceId,
          label: d.label || `Output ${i + 1}`,
        }));

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
      const activeDeviceMatrix =
        Object.keys(currentMatrixByView.device || {}).length > 0
          ? currentMatrixByView.device
          : saved?.matrixByView?.device || saved?.matrixState || {};
      const activeChannelMatrix =
        Object.keys(currentMatrixByView.channel || {}).length > 0
          ? currentMatrixByView.channel
          : saved?.matrixByView?.channel || {};
      setInputs(orderedInputs);
      setOutputs(orderedOutputs);
      devicesDiscoveredRef.current = true;

      const nextInputLabels = createLabelMap(discoveredInputs, saved?.inputLabels || {}, "Input");
      const nextOutputLabels = createLabelMap(discoveredOutputs, saved?.outputLabels || {}, "Output");

      const deviceRows = orderedInputs.map((i) => ({ id: `dev:${i.deviceId}` }));
      const deviceCols = orderedOutputs.map((o) => ({ id: `dev:${o.deviceId}` }));

      const channelRows = orderedInputs.flatMap((i) => [
        { id: `ch:${i.deviceId}:0` },
        { id: `ch:${i.deviceId}:1` },
      ]);

      const channelCols = orderedOutputs.flatMap((o) => [
        { id: `ch:${o.deviceId}:0` },
        { id: `ch:${o.deviceId}:1` },
      ]);

      const sourceDeviceMatrix = Object.keys(currentMatrixByView.device || {}).length > 0
        ? currentMatrixByView.device
        : saved?.matrixByView?.device || saved?.matrixState || {};
      const sourceChannelMatrix = Object.keys(currentMatrixByView.channel || {}).length > 0
        ? currentMatrixByView.channel
        : saved?.matrixByView?.channel || {};

      const nextMatrixByView = {
        device: createMatrix(deviceRows, deviceCols, sourceDeviceMatrix),
        channel: createMatrix(channelRows, channelCols, sourceChannelMatrix),
      };

      const nextViewMode = saved?.viewMode === "channel" ? "channel" : "device";
      const hasSavedLabelSizing = !!saved?.labelSizing;
      const fallbackSource = DEFAULT_LABEL_SQUARE_SIZE;
      const seedSquare = clamp(
        hasSavedLabelSizing
          ? saved?.labelSizing?.sourceWidth ?? saved?.labelSizing?.destinationHeight ?? fallbackSource
          : fallbackSource,
        LABEL_SQUARE_MIN,
        LABEL_SQUARE_MAX,
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

      const nextInputMaster = orderedInputs.some((d) => d.deviceId === saved?.inputMasterId) ? saved?.inputMasterId || "" : "";
      const nextOutputMaster = orderedOutputs.some((d) => d.deviceId === saved?.outputMasterId) ? saved?.outputMasterId || "" : "";
      setInputMasterId(nextInputMaster);
      setOutputMasterId(nextOutputMaster);
      if (nextInputMaster) pushInputMasterToNative(nextInputMaster);
      if (nextOutputMaster) pushOutputMasterToNative(nextOutputMaster);

      const snapshot = buildPersistedState({
        backgroundKey: saved?.backgroundKey || BACKGROUND_PRESETS[backgroundIndex]?.key,
        accentKey: saved?.accentKey || ACCENT_PRESETS[accentIndex]?.key,
        fontKey: saved?.fontKey || FONT_PRESETS[fontIndex]?.key,
        fontSizeKey: saved?.fontSizeKey || FONT_SIZE_PRESETS[fontSizeIndex]?.key,
        controlsCollapsed: typeof saved?.controlsCollapsed === "boolean" ? saved.controlsCollapsed : controlsCollapsed,
        showAllDevices: typeof saved?.showAllDevices === "boolean" ? saved.showAllDevices : showAllDevices,
        powerOn: typeof saved?.powerOn === "boolean" ? saved.powerOn : powerOn,
        locked: hasNativeBridge ? locked : !!saved?.locked,
        inputLabels: nextInputLabels,
        outputLabels: nextOutputLabels,
        inputMasterId: nextInputMaster,
        outputMasterId: nextOutputMaster,
        viewMode: nextViewMode,
        labelSizing: nextLabelSizing,
        matrixByView: nextMatrixByView,
        inputOrder: orderedInputs.map((d) => d.deviceId),
        outputOrder: orderedOutputs.map((d) => d.deviceId),
      });
      persistState(snapshot);

      const discoveredConfigured = collectConfiguredDeviceIds(nextMatrixByView);
      const discoveredRoutedInputs = orderedInputs.filter((d) => discoveredConfigured.configuredInputIds.has(d.deviceId));
      const discoveredRoutedOutputs = orderedOutputs.filter((d) => discoveredConfigured.configuredOutputIds.has(d.deviceId));
      const setupInputs = locked ? discoveredRoutedInputs : (showAllDevices ? orderedInputs : discoveredRoutedInputs);
      const setupOutputs = locked ? discoveredRoutedOutputs : (showAllDevices ? orderedOutputs : discoveredRoutedOutputs);

      if (powerOn && setupInputs.length > 0 && setupOutputs.length > 0) {
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
    if (locked) return;
    setSelectedCell(null);
    setTileMenuCell(null);
    setGainAdjustCell(null);

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
    await discoverDevices();
  };

  const handleResetMatrix = async (event) => {
    event?.stopPropagation?.();

    const deviceRows = inputs.map((input) => ({ id: `dev:${input.deviceId}` }));
    const deviceCols = outputs.map((output) => ({ id: `dev:${output.deviceId}` }));
    const channelRows = inputs.flatMap((input) => [
      { id: `ch:${input.deviceId}:0` },
      { id: `ch:${input.deviceId}:1` },
    ]);
    const channelCols = outputs.flatMap((output) => [
      { id: `ch:${output.deviceId}:0` },
      { id: `ch:${output.deviceId}:1` },
    ]);

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
  }, [inputs, outputs, viewMode, inputLabels, outputLabels, inputMasterId, outputMasterId, labelSizing, matrixByView]);

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
    const tick = () => {
      const nextInput = {};
      rows.forEach((row) => {
        nextInput[row.id] = managerRef.current.getInputLevel(row.id);
      });

      if (modeRef.current === "device") {
        inputs.forEach((input) => {
          const leftId = `ch:${input.deviceId}:0`;
          const rightId = `ch:${input.deviceId}:1`;
          nextInput[leftId] = managerRef.current.getInputLevel(leftId);
          nextInput[rightId] = managerRef.current.getInputLevel(rightId);
        });
      }

      const nextOutput = {};
      cols.forEach((col) => {
        nextOutput[col.id] = managerRef.current.getOutputLevel(col.id);
      });

      if (modeRef.current === "device") {
        outputs.forEach((output) => {
          const leftId = `ch:${output.deviceId}:0`;
          const rightId = `ch:${output.deviceId}:1`;
          nextOutput[leftId] = managerRef.current.getOutputLevel(leftId);
          nextOutput[rightId] = managerRef.current.getOutputLevel(rightId);
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
  }, [rows, cols, routedInputs, routedOutputs]);

  useEffect(() => {
    if (inputs.length === 0 || outputs.length === 0) return;

    rebuildAudioGraph(viewMode, matrixRef.current);
    setSelectedCell(null);
  }, [viewMode]);

  useEffect(() => {
    if (!powerOn) return;
    applyMatrixToEngine(viewMode, matrixRef.current[viewMode] || {});
  }, [transientMuteAll, powerOn, viewMode]);

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

  const updateConnection = (rowId, colId, updater) => {
    if (locked) return;

    const key = getCellKey(rowId, colId);
    let shouldReloadForMasterSwitch = false;
    let nextInputMasterId = inputMasterId;
    let nextOutputMasterId = outputMasterId;
    let updatedConnection = null;

    setMatrixByView((prev) => {
      const currentModeMatrix = prev[viewMode] || {};
      const currentConnection = currentModeMatrix[key] || makeDefaultConnection();
      const rawNextConnection = typeof updater === "function" ? updater(currentConnection) : updater;
      const nextConnection = sanitizeConnection(rawNextConnection);
      updatedConnection = nextConnection;

      if (nextConnection.on) {
        const pair = getDevicePairFromRoute(rowId, colId);
        if (!inputMasterId && pair.inputDeviceId) {
          setInputMaster(pair.inputDeviceId, true);
          nextInputMasterId = pair.inputDeviceId;
          shouldReloadForMasterSwitch = true;
        }
        if (!outputMasterId && pair.outputDeviceId) {
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
        const rowParts = String(rowId).split(":");
        const colParts = String(colId).split(":");
        const inDev = rowParts[0] === "ch" ? rowParts[1] : "";
        const outDev = colParts[0] === "ch" ? colParts[1] : "";

        if (inDev && outDev) {
          const channelKeys = [
            getCellKey(`ch:${inDev}:0`, `ch:${outDev}:0`),
            getCellKey(`ch:${inDev}:0`, `ch:${outDev}:1`),
            getCellKey(`ch:${inDev}:1`, `ch:${outDev}:0`),
            getCellKey(`ch:${inDev}:1`, `ch:${outDev}:1`),
          ];

          const connections = channelKeys.map((k) => nextModeMatrix[k] || makeDefaultConnection());
          const anyOn = connections.some((c) => c.on);
          const allMuted = anyOn ? connections.every((c) => c.muted || !c.on) : false;
          const avgGainDb = connections.reduce((acc, c) => acc + (Number.isFinite(c.gainDb) ? c.gainDb : 0), 0) / connections.length;

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
      }

      const deviceMatrix = next.device || {};
      const isInputDeviceUsed = (deviceId) =>
        Object.entries(deviceMatrix).some(([routeKey, conn]) => conn?.on && routeKey.startsWith(`dev:${deviceId}::`));
      const isOutputDeviceUsed = (deviceId) =>
        Object.entries(deviceMatrix).some(([routeKey, conn]) => conn?.on && routeKey.includes(`::dev:${deviceId}`));

      if (nextConnection.on) {
        const pair = getDevicePairFromRoute(rowId, colId);

        if (
          inputMasterId &&
          pair.inputDeviceId &&
          inputMasterId !== pair.inputDeviceId &&
          !isInputDeviceUsed(inputMasterId)
        ) {
          setInputMaster(pair.inputDeviceId, true);
          nextInputMasterId = pair.inputDeviceId;
          shouldReloadForMasterSwitch = true;
        }

        if (
          outputMasterId &&
          pair.outputDeviceId &&
          outputMasterId !== pair.outputDeviceId &&
          !isOutputDeviceUsed(outputMasterId)
        ) {
          setOutputMaster(pair.outputDeviceId, true);
          nextOutputMasterId = pair.outputDeviceId;
          shouldReloadForMasterSwitch = true;
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

    if (hasNativeBridge && updatedConnection) {
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
          const nextChannelMatrix = convertDeviceMatrixToChannelMatrix(prevMatrix.device || {}, prevMatrix.channel || {});
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
    if (locked) return;
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
    if (activeQuickPicker === "startup") return startupAtBoot ? "enable" : "disable";
    return "";
  })();

  const applyQuickSelection = (type, key) => {
    if (type === "background") setBackgroundIndex(Math.max(0, BACKGROUND_PRESETS.findIndex((p) => p.key === key)));
    if (type === "accent") setAccentIndex(Math.max(0, ACCENT_PRESETS.findIndex((p) => p.key === key)));
    if (type === "font") setFontIndex(Math.max(0, FONT_PRESETS.findIndex((p) => p.key === key)));
    if (type === "fontSize") setFontSizeIndex(Math.max(0, FONT_SIZE_PRESETS.findIndex((p) => p.key === key)));
    if (type === "startup") handleSetStartupAtBoot(key === "enable");
  };

  const adjustGainForCell = (rowId, colId, stepDb) => {
    if (locked) return;
    const key = getCellKey(rowId, colId);
    const state = activeMatrix[key] || makeDefaultConnection();
    updateConnection(rowId, colId, {
      ...state,
      on: true,
      gainDb: clamp((Number.isFinite(state.gainDb) ? state.gainDb : 0) + stepDb, DB_MIN, DB_MAX),
    });
  };

  const openTileMenuForCell = (event, rowId, colId) => {
    if (locked) return;
    const menuHeightEstimate = 130;
    const nearBottom = event.clientY > window.innerHeight - menuHeightEstimate;
    setTileMenuCell({ rowId, colId, dropUp: nearBottom });
    setGainAdjustCell(null);
  };

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

      if (state.mode === "source-width") {
        const nextSquare = clamp(state.startWidth + (event.clientX - state.startX), LABEL_SQUARE_MIN, LABEL_SQUARE_MAX);
        setLabelSizing({
          sourceWidth: nextSquare,
          destinationHeight: nextSquare,
        });
        document.body.style.cursor = "nwse-resize";
      }

      if (state.mode === "destination-height") {
        const nextSquare = clamp(state.startHeight + (event.clientY - state.startY), LABEL_SQUARE_MIN, LABEL_SQUARE_MAX);
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

      persistState({
        viewMode,
        inputLabels,
        outputLabels,
        inputMasterId,
        outputMasterId,
        labelSizing,
        matrixByView,
      });
    };

    window.addEventListener("mousemove", onMove);
    window.addEventListener("mouseup", onUp);

    return () => {
      window.removeEventListener("mousemove", onMove);
      window.removeEventListener("mouseup", onUp);
      document.body.style.cursor = "";
    };
  }, [labelSizing, viewMode, inputLabels, outputLabels, inputMasterId, outputMasterId, matrixByView]);

  const selectedRouteText = detailCell
    ? `${rows.find((r) => r.id === detailCell.rowId)?.label || "Input"} -> ${
        cols.find((c) => c.id === detailCell.colId)?.label || "Output"
      }`
    : "Select a square in the matrix";

  const selectedSource = detailCell ? rows.find((r) => r.id === detailCell.rowId) : null;
  const selectedDestination = detailCell ? cols.find((c) => c.id === detailCell.colId) : null;
  const getRowSplitLevels = (row) => {
    if (!row) return [0, 0];
    const devId = row.deviceId || (row.id.startsWith("dev:") ? row.id.slice(4) : row.id.split(":")[1]);
    const leftId = `ch:${devId}:0`;
    const rightId = `ch:${devId}:1`;
    return [
      inputLevels[leftId] ?? managerRef.current.getInputLevel(leftId) ?? 0,
      inputLevels[rightId] ?? managerRef.current.getInputLevel(rightId) ?? 0,
    ];
  };

  const getColSplitLevels = (col) => {
    if (!col) return [0, 0];
    const devId = col.outputDeviceId || outputDeviceFromColId(col.id);
    const leftId = `ch:${devId}:0`;
    const rightId = `ch:${devId}:1`;
    return [
      outputLevels[leftId] ?? managerRef.current.getOutputLevel(leftId) ?? 0,
      outputLevels[rightId] ?? managerRef.current.getOutputLevel(rightId) ?? 0,
    ];
  };

  const selectedSourceSplit = selectedSource ? getRowSplitLevels(selectedSource) : [0, 0];
  const selectedDestinationSplit = selectedDestination ? getColSplitLevels(selectedDestination) : [0, 0];
  const hasAnyActiveRoute = Object.values(activeMatrix).some((conn) => conn.on);
  const muteButtonIsMuted = isHoverDetail
    ? !!selectedConnection?.muted
    : transientMuteAll;
  const latencyLabel = latencyMs != null ? `${latencyMs}ms` : "n/a";
  const jitterLabel = jitterMs != null ? `${jitterMs}ms` : "n/a";
  const bufferLabel = bufferMs != null ? `${bufferMs}ms` : "n/a";
  const clockLabel = clockKhz != null ? `${clockKhz}kHz` : "n/a";

  const handleTransientMuteAllToggle = () => {
    if (!hasAnyActiveRoute) return;
    setTransientMuteAll((prev) => !prev);
  };

  const handleRootClick = (event) => {
    if (event.target.closest(".matrix-grid")) return;
    if (event.target.closest(".mute-btn")) return;
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
            <p>{contextState === "running" ? `Running${latencyMs != null ? ` · ${latencyMs}ms` : ""}` : "Standby"}</p>
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
            {controlsCollapsed ? "⚙" : "✕"}
          </button>
        </div>

      </header>

      {error ? <div className="error-banner">{error}</div> : null}

      <main className="main-layout">
        <section className="matrix-wrap rack-panel">
          <div
            className={`matrix-grid${selectedCell ? " has-selection" : ""}`}
            style={{
              gridTemplateColumns: `${labelSizing.sourceWidth}px repeat(${Math.max(cols.length, 1)}, ${cellSize}px)`,
              gridTemplateRows: `${labelSizing.destinationHeight}px`,
              gridAutoRows: `${cellSize}px`,
            }}
            onMouseLeave={() => {
              if (locked) return;
              if (!tileMenuCell && !gainAdjustCell) {
                setSelectedCell(null);
              }
            }}
          >
            <div className="matrix-corner matrix-corner-content">
              <div className="corner-controls" role="group" aria-label="Matrix quick controls">
                <button
                  type="button"
                  className="corner-control-btn corner-control-tl"
                  onClick={handleReloadDevices}
                  disabled={locked || isReloadingDevices}
                  title="Restart and reload devices"
                  aria-label="Restart and reload devices"
                >
                  <span aria-hidden="true">↻</span>
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
                  className={`corner-control-btn corner-control-mid ${powerOn ? "active" : ""}`}
                  onClick={togglePowerState}
                  title={powerOn ? "Power on (click to power off)" : "Power off (click to power on)"}
                  aria-label="Toggle power"
                  disabled={locked}
                >
                  <span aria-hidden="true">⏻</span>
                </button>
              </div>
            </div>
            <button
              type="button"
              className="resize-handle grid-resize-handle grid-source-width-handle"
              style={{ left: `${labelSizing.sourceWidth - 3}px` }}
              onMouseDown={beginResizeSourceWidth}
              aria-label="Resize source label width"
            />
            <button
              type="button"
              className="resize-handle grid-resize-handle grid-dest-height-handle"
              style={{ top: `${labelSizing.destinationHeight - 3}px` }}
              onMouseDown={beginResizeDestinationHeight}
              aria-label="Resize destination label height"
            />

            {cols.map((col) => {
              if (col.isChannelEnd) return null;

              const isActive = activeRoutes.has(col.id) || (col.pairedId && activeRoutes.has(col.pairedId));
              const span = col.isChannelStart ? 2 : 1;
              const [colLevelL, colLevelR] = getColSplitLevels(col);

              const isColSelected = selectedCell?.colId === col.id || (col.pairedId && selectedCell?.colId === col.pairedId);
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
                    <div className="card-meter-bg card-meter-bg-col-split" aria-hidden="true">
                      <span className="meter-bar meter-bar-l" style={{ height: `${Math.round(colLevelL * 100)}%` }} />
                      <span className="meter-bar meter-bar-r" style={{ height: `${Math.round(colLevelR * 100)}%` }} />
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
                  <span className="col-channels-box" aria-hidden="true">
                    <span className="axis-split-label axis-split-cell axis-label-l">L</span>
                    <span className="axis-split-label axis-split-cell axis-label-r">R</span>
                  </span>
                </div>
              );
            })}

            {rows.map((row, rowIndex) => {
              const isRowActive = activeRoutes.has(row.id) || (row.pairedId && activeRoutes.has(row.pairedId));
              const isRowAxisActive = selectedCell?.rowId === row.id || (row.pairedId && selectedCell?.rowId === row.pairedId);
              const skipHead = row.isChannelEnd;
              const [rowLevelL, rowLevelR] = getRowSplitLevels(row);
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
                  style={row.isChannelStart ? { gridRow: "span 2" } : undefined}
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
                    <div className="card-meter-bg card-meter-bg-row-split" aria-hidden="true">
                      <span className="meter-bar meter-bar-l" style={{ width: `${Math.round(rowLevelL * 100)}%` }} />
                      <span className="meter-bar meter-bar-r" style={{ width: `${Math.round(rowLevelR * 100)}%` }} />
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
                  <span className="row-channels-box" aria-hidden="true">
                    <span className="axis-split-label axis-split-cell axis-label-l">L</span>
                    <span className="axis-split-label axis-split-cell axis-label-r">R</span>
                  </span>
                </div>
                )}

                {cols.map((col) => {
                  const key = getCellKey(row.id, col.id);
                  const state = activeMatrix[key] || makeDefaultConnection();
                  const selected = selectedCell?.rowId === row.id && selectedCell?.colId === col.id;
                  const isMenuOpen = tileMenuCell?.rowId === row.id && tileMenuCell?.colId === col.id;
                  const isMenuDropUp = !!tileMenuCell?.dropUp;
                  const isGainAdjustOpen = gainAdjustCell?.rowId === row.id && gainAdjustCell?.colId === col.id;
                  return (
                    <div key={key} className="tile-cell-wrap" style={{ width: `${cellSize}px`, height: `${cellSize}px` }}>
                      <button
                        type="button"
                        className={[
                          "cell",
                          state.on ? "on" : "off",
                          selected && "selected",
                          state.on && state.muted && "muted",
                          state.phaseInverted && "phase-inverted",
                          selectedCell && row.id === selectedCell.rowId && "active-row",
                          selectedCell && col.id === selectedCell.colId && "active-col",
                        ].filter(Boolean).join(" ")}
                        style={{ width: `${cellSize}px`, height: `${cellSize}px` }}
                        onMouseEnter={() => {
                          if (!locked) {
                            setSelectedCell({ rowId: row.id, colId: col.id });
                          }
                        }}
                        onClick={() =>
                          updateConnection(row.id, col.id, {
                            ...state,
                            on: !state.on,
                            muted: false,
                          })
                        }
                        onContextMenu={(event) => {
                          if (locked) return;
                          event.preventDefault();
                          event.stopPropagation();
                          setSelectedCell({ rowId: row.id, colId: col.id });
                          if (isMenuOpen) {
                            setTileMenuCell(null);
                            setGainAdjustCell(null);
                            return;
                          }
                          openTileMenuForCell(event, row.id, col.id);
                        }}
                        title={`${row.label} -> ${col.label}`}
                        disabled={locked}
                      >
                        {Math.abs(state.gainDb || 0) >= 0.5 ? (
                          <span className="tile-gain-readout">{`${state.gainDb > 0 ? "+" : ""}${Math.round(state.gainDb)}dB`}</span>
                        ) : null}
                      </button>
                      {isMenuOpen ? (
                        <div className={`tile-context-menu ${isMenuDropUp ? "drop-up" : ""}`} onClick={(event) => event.stopPropagation()}>
                          <button
                            type="button"
                            className={`tile-menu-btn ${state.phaseInverted ? "active" : ""}`}
                            onClick={() =>
                              updateConnection(row.id, col.id, {
                                ...state,
                                on: true,
                                phaseInverted: !state.phaseInverted,
                              })
                            }
                            title="Flip phase"
                            aria-label="Flip phase"
                            disabled={locked}
                          >
                            Ø
                          </button>

                          <div className="tile-gain-wrap">
                            <button
                              type="button"
                              className={`tile-menu-btn tile-gain-btn ${isGainAdjustOpen ? "active" : ""}`}
                              onClick={() =>
                                setGainAdjustCell((prev) =>
                                  prev?.rowId === row.id && prev?.colId === col.id ? null : { rowId: row.id, colId: col.id }
                                )
                              }
                              onWheel={(event) => {
                                if (locked) return;
                                if (!isGainAdjustOpen) return;
                                event.preventDefault();
                                event.stopPropagation();
                                adjustGainForCell(row.id, col.id, event.deltaY < 0 ? 1 : -1);
                              }}
                              title="Edit gain"
                              aria-label="Edit gain"
                              disabled={locked}
                            >
                              {`${Math.round(state.gainDb || 0)} dB`}
                            </button>
                            {isGainAdjustOpen ? (
                              <>
                                <button
                                  type="button"
                                  className="tile-gain-step tile-gain-up"
                                  onClick={() => adjustGainForCell(row.id, col.id, 1)}
                                  title="Increase gain"
                                  aria-label="Increase gain"
                                  disabled={locked}
                                >
                                  ▲
                                </button>
                                <button
                                  type="button"
                                  className="tile-gain-step tile-gain-down"
                                  onClick={() => adjustGainForCell(row.id, col.id, -1)}
                                  title="Decrease gain"
                                  aria-label="Decrease gain"
                                  disabled={locked}
                                >
                                  ▼
                                </button>
                              </>
                            ) : null}
                          </div>

                          <button
                            type="button"
                            className={`tile-menu-btn ${state.muted ? "active warn" : ""}`}
                            onClick={() =>
                              updateConnection(row.id, col.id, {
                                ...state,
                                on: true,
                                muted: !state.muted,
                              })
                            }
                            title="Toggle mute"
                            aria-label="Toggle mute"
                            disabled={locked}
                          >
                            M
                          </button>
                        </div>
                      ) : null}
                    </div>
                  );
                })}
              </React.Fragment>
              );
            })}
          </div>

        </section>
      </main>

      {detailCell && selectedConnection ? (
      <div className="inline-editor docked rack-panel">
        <div className="dock-col dock-card">
          <div className="card-main-copy card-main-copy-split">
            <div className="card-meter-bg card-meter-bg-row-split" aria-hidden="true">
              <span className="meter-bar meter-bar-l" style={{ width: `${Math.round(selectedSourceSplit[0] * 100)}%` }} />
              <span className="meter-bar meter-bar-r" style={{ width: `${Math.round(selectedSourceSplit[1] * 100)}%` }} />
            </div>
            <p>{selectedSource?.label || "Source"}</p>
            {selectedSource?.hardwareLabel ? <p className="detail-line">{selectedSource.hardwareLabel}</p> : null}
          </div>
          <div className="card-metrics-box">
            <div className="metric-tile">
              <span className="metric-title">Latency</span>
              <span className="metric-value">{latencyLabel}</span>
            </div>
            <div className="metric-tile">
              <span className="metric-title">Timing</span>
              <span className="metric-value">{`Jit ${jitterLabel}`}</span>
            </div>
          </div>
        </div>

        <div className="dock-col dock-center">
            <button
              type="button"
              className={`mute-btn ${muteButtonIsMuted ? "muted" : ""}`}
              disabled={!hasAnyActiveRoute}
              onClick={handleTransientMuteAllToggle}
              title={isHoverDetail
                ? `Tile mute: ${selectedConnection?.muted ? "muted" : "unmuted"}. Click for transient mute all.`
                : transientMuteAll
                  ? "Transient mute all is ON. Click to restore previous mute state."
                  : "Global mute status is OFF. Click for transient mute all."}
            >
              {muteButtonIsMuted ? <VolumeX size={14} /> : <Volume2 size={14} />}
            </button>
        </div>

        <div className="dock-col dock-card">
          <div className="card-main-copy card-main-copy-split">
            <div className="card-meter-bg card-meter-bg-row-split" aria-hidden="true">
              <span className="meter-bar meter-bar-l" style={{ width: `${Math.round(selectedDestinationSplit[0] * 100)}%` }} />
              <span className="meter-bar meter-bar-r" style={{ width: `${Math.round(selectedDestinationSplit[1] * 100)}%` }} />
            </div>
            <p>{selectedDestination?.label || "Destination"}</p>
            {selectedDestination?.hardwareLabel ? <p className="detail-line">{selectedDestination.hardwareLabel}</p> : null}
          </div>
          <div className="card-metrics-box">
            <div className="metric-tile">
              <span className="metric-title">Latency</span>
              <span className="metric-value">{latencyLabel}</span>
            </div>
            <div className="metric-tile">
              <span className="metric-title">Timing</span>
              <span className="metric-value">{`Jit ${jitterLabel}`}</span>
            </div>
          </div>
        </div>
      </div>
      ) : null}
    </div>
  );
}
