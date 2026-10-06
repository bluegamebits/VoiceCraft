// VoiceCraft web client library (ES module). Talks to VoiceCraft.Web over a WebSocket.
//
//   import { VoiceCraftWeb } from './voicecraft-web.js';
//   const vc = new VoiceCraftWeb({ url: 'wss://example.com/ws' });
//   vc.addEventListener('bindingkey', (e) => show(`/vcbind ${e.detail.key}`));
//   button.onclick = () => vc.start(); // must run from a user gesture (microphone + audio)
//
// Events (CustomEvent, data in e.detail): state {state, reason}, title {text}, description {text},
// bindingkey {key, linkedName}, linked {name}, bound {name}, speaking {value}, level {rms, peak}, peers {list}, muted {value},
// deafened {value}, serverMuted {value}, serverDeafened {value}, global {talk, listen},
// microphone {deviceId, label, fallback}, speaker {deviceId, fallback}, error {error}.
// state is one of: idle, starting, connecting, connected, reconnecting, stopped.
// global is only sent by servers with the global channel: it confirms what the server applied.
// microphone/speaker report the device in use; fallback means the chosen one wasn't available, so the
// system default is used.

const FRAME = 960; // 20 ms at 48 kHz
const DEFAULT_SENSITIVITY = 0.04; // the bridge's default, same as the native app
const PCM_RATE = 16000;
const PCM_FRAME = 320;
const ID_KEY = 'voicecraft-web-ids';

const BINDING_KEY = /binding key is\s+([0-9A-Za-z]{5})/i;
const BOUND_TO = /^Bound to player\s+(.+)$/i;
// Sent by add-ons that remember devices: this device is linked, waiting for the player to join.
const LINKED_TO = /^Linked to player\s+(.+?)\.\s/i;

export class VoiceCraftWeb extends EventTarget {
  /**
   * @param {object} options
   * @param {string} options.url WebSocket URL of the bridge (wss://…/ws).
   * @param {string} [options.workletUrl] URL of voicecraft-worklet.js (default: next to this module).
   * @param {'opus'|'pcm16'} [options.codec] Force a codec (default: opus when the browser supports it).
   * @param {MediaTrackConstraints} [options.microphone] Extra getUserMedia audio constraints.
   * @param {string} [options.microphoneId] Microphone to use (a deviceId from listDevices()); default: the system's.
   * @param {string} [options.speakerId] Output device to play to, where canChooseSpeaker; default: the system's.
   * @param {number} [options.volume] Output volume, 0..2 (1 = normal).
   * @param {number} [options.inputVolume] Microphone volume, 0..2 (1 = as recorded).
   * @param {number} [options.sensitivity] Voice activation threshold, 0..1 (a frame's peak level).
   * @param {{echoCancellation?: boolean, noiseSuppression?: boolean, autoGainControl?: boolean}} [options.processing]
   *   The browser's microphone processing (all on by default).
   * @param {(ctx: AudioContext) => MediaStream} [options.stream] Use this stream instead of the microphone (testing).
   */
  constructor(options) {
    super();
    this.options = options;
    this.microphoneId = options.microphoneId || '';
    this.speakerId = options.speakerId || '';
    this.processing = { echoCancellation: true, noiseSuppression: true, autoGainControl: true, ...(options.processing || {}) };
    this.state = 'idle';
    this.bindingKey = null;
    this.boundName = null;
    this.peers = [];
    this.muted = false;
    this.deafened = false;
    this.volume = options.volume === undefined ? 1 : clamp(options.volume, 0, 2);
    this.inputVolume = options.inputVolume === undefined ? 1 : clamp(options.inputVolume, 0, 2);
    this.sensitivity = options.sensitivity === undefined ? DEFAULT_SENSITIVITY : clamp(options.sensitivity, 0, 1);
    /** Global channel as confirmed by the server ({talk, listen}), or null if it hasn't confirmed (yet). */
    this.global = null;
    this._globalTalk = false;
    this._globalListen = true;
    this._ws = null;
    this._retry = 0;
    this._stopped = true;
    this._timestamp = 0;
    /** Diagnostics: frames sent/received and the level of the last played frame. */
    this.stats = { sent: 0, received: 0, playedRms: 0 };
  }

  /**
   * Forgets this browser's ids, so the server no longer recognizes it as a linked device
   * (the next connection needs /vcbind again). Takes effect on the next connection.
   */
  static forgetDevice() {
    try { localStorage.removeItem(ID_KEY); } catch { /* storage unavailable */ }
  }

  /** True if this browser can play to a chosen output device (AudioContext.setSinkId: Chrome and Edge). */
  static get canChooseSpeaker() {
    return typeof AudioContext !== 'undefined' && typeof AudioContext.prototype.setSinkId === 'function';
  }

  /**
   * Microphones and output devices, as {deviceId, label}. Labels need microphone permission, so call this
   * after start(). Outputs are only listed where canChooseSpeaker. '' (not listed) is the system default.
   */
  static async listDevices() {
    const devices = navigator.mediaDevices && navigator.mediaDevices.enumerateDevices
      ? await navigator.mediaDevices.enumerateDevices() : [];
    // Chrome adds "default" and "communications" entries that repeat a real device.
    const real = (kind) => devices
      .filter((d) => d.kind === kind && d.deviceId && d.deviceId !== 'default' && d.deviceId !== 'communications')
      .map((d) => ({ deviceId: d.deviceId, label: d.label || '' }));
    return {
      microphones: real('audioinput'),
      speakers: VoiceCraftWeb.canChooseSpeaker ? real('audiooutput') : [],
    };
  }

  /** Opus via WebCodecs if the browser can encode and decode it, else 16 kHz PCM. */
  static async detectCodec() {
    try {
      if (typeof AudioEncoder === 'undefined' || typeof AudioDecoder === 'undefined') return 'pcm16';
      const enc = await AudioEncoder.isConfigSupported({ codec: 'opus', sampleRate: 48000, numberOfChannels: 1, bitrate: 32000 });
      const dec = await AudioDecoder.isConfigSupported({ codec: 'opus', sampleRate: 48000, numberOfChannels: 2 });
      return enc.supported && dec.supported ? 'opus' : 'pcm16';
    } catch {
      return 'pcm16';
    }
  }

  /** Starts microphone + audio and connects. Call from a click/tap handler. */
  async start() {
    if (!this._stopped) return;
    this._stopped = false;
    this._setState('starting');
    try {
      // AudioContext first: on iOS it must be created inside the user gesture.
      const AC = window.AudioContext || window.webkitAudioContext;
      try {
        this._ctx = new AC({ sampleRate: 48000, latencyHint: 'interactive' });
      } catch {
        this._ctx = new AC({ latencyHint: 'interactive' });
      }
      const ctxResume = this._ctx.resume();
      this._stream = this.options.stream ? this.options.stream(this._ctx) : await this._openMicrophone(this.microphoneId);
      await ctxResume;
      if (this.speakerId) await this._applySpeaker();
      const workletUrl = this.options.workletUrl || new URL('./voicecraft-worklet.js', import.meta.url).href;
      await this._ctx.audioWorklet.addModule(workletUrl);

      this.codec = this.options.codec || (await VoiceCraftWeb.detectCodec());
      this._setupCodec();

      // microphone → input volume → capture (→ silent sink). The volume is applied here, before the level
      // meter and before the bridge's voice activation, so both see what the others will hear.
      this._source = this._ctx.createMediaStreamSource(this._stream);
      this._gain = this._ctx.createGain();
      this._gain.gain.value = this.inputVolume;
      this._capture = new AudioWorkletNode(this._ctx, 'vc-capture', { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1] });
      this._capture.port.onmessage = (e) => this._onMicFrame(e.data.frame, e.data.rms, e.data.peak);
      this._sink = this._ctx.createGain();
      this._sink.gain.value = 0; // keeps the capture node running without echoing the mic
      this._source.connect(this._gain).connect(this._capture).connect(this._sink).connect(this._ctx.destination);

      this._playback = new AudioWorkletNode(this._ctx, 'vc-playback', { numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [2] });
      this._playback.connect(this._ctx.destination);

      this._onVisibility = () => {
        if (document.visibilityState === 'visible') {
          if (this._ctx && this._ctx.state !== 'running') this._ctx.resume().catch(() => {});
          this._requestWakeLock();
        }
      };
      document.addEventListener('visibilitychange', this._onVisibility);
      this._requestWakeLock();
      this._connect();
    } catch (error) {
      this._emit('error', { error });
      await this.stop(error && error.name === 'NotAllowedError' ? 'microphone-denied' : 'start-failed');
      throw error;
    }
  }

  /** Disconnects and releases the microphone. */
  async stop(reason) {
    this._stopped = true;
    clearTimeout(this._retryTimer);
    if (this._ws) {
      try { this._ws.close(1000, 'stop'); } catch { /* ignore */ }
      this._ws = null;
    }
    if (this._stream) this._stream.getTracks().forEach((t) => t.stop());
    this._stream = null;
    this._source = null;
    this._gain = null;
    if (this._encoder && this._encoder.state !== 'closed') try { this._encoder.close(); } catch { /* ignore */ }
    if (this._decoder && this._decoder.state !== 'closed') try { this._decoder.close(); } catch { /* ignore */ }
    this._encoder = this._decoder = null;
    if (this._ctx) await this._ctx.close().catch(() => {});
    this._ctx = null;
    if (this._onVisibility) document.removeEventListener('visibilitychange', this._onVisibility);
    if (this._wakeLock) this._wakeLock.release().catch(() => {});
    this._wakeLock = null;
    this.bindingKey = null;
    this.boundName = null;
    this.peers = [];
    this.global = null;
    this._setState('stopped', reason);
  }

  setMuted(value) { this.muted = !!value; this._send({ t: 'mute', value: this.muted }); }
  setDeafened(value) { this.deafened = !!value; this._send({ t: 'deafen', value: this.deafened }); }
  /** Output volume, 0..2 (1 = normal). */
  setVolume(value) { this.volume = clamp(value, 0, 2); this._send({ t: 'volume', value: this.volume }); }
  /**
   * Voice activation threshold, 0..1 (lower = more sensitive): the bridge sends your voice while a 20 ms
   * frame's peak (the 'level' event's peak, after the input volume) reaches it.
   */
  setSensitivity(value) { this.sensitivity = clamp(value, 0, 1); this._send({ t: 'sensitivity', value: this.sensitivity }); }
  /** Microphone volume, 0..2 (1 = as recorded), applied in the browser. */
  setInputVolume(value) {
    this.inputVolume = clamp(value, 0, 2);
    if (this._gain && this._ctx) this._gain.gain.setTargetAtTime(this.inputVolume, this._ctx.currentTime, 0.02);
  }
  /**
   * The browser's microphone processing; reopens the microphone if started.
   * @param {{echoCancellation?: boolean, noiseSuppression?: boolean, autoGainControl?: boolean}} processing
   */
  async setProcessing(processing) {
    this.processing = { ...this.processing, ...processing };
    if (this._stopped || !this._ctx || !this._capture || this.options.stream) return;
    await this._swapMicrophone(this.microphoneId);
  }
  /**
   * Global channel: talk to everyone instead of players nearby, and/or stop hearing it. Can be called before
   * start(); kept across reconnects. The server applies it once the player is linked, then sends 'global'.
   * @param {{talk?: boolean, listen?: boolean}} choice
   */
  setGlobal({ talk, listen } = {}) {
    if (talk !== undefined) this._globalTalk = !!talk;
    if (listen !== undefined) this._globalListen = !!listen;
    this._send({ t: 'global', talk: this._globalTalk, listen: this._globalListen });
  }

  /** Switches the microphone ('' = system default), live if started. A deviceId from listDevices(). */
  async setMicrophone(deviceId) {
    this.microphoneId = deviceId || '';
    if (this._stopped || !this._ctx || !this._capture || this.options.stream) return;
    await this._swapMicrophone(this.microphoneId);
  }

  /** Switches the output device ('' = system default), where canChooseSpeaker. A deviceId from listDevices(). */
  async setSpeaker(deviceId) {
    this.speakerId = deviceId || '';
    await this._applySpeaker();
  }

  // ── Devices ────────────────────────────────────────────

  async _openMicrophone(deviceId) {
    const stream = await navigator.mediaDevices.getUserMedia({
      audio: {
        echoCancellation: this.processing.echoCancellation,
        noiseSuppression: this.processing.noiseSuppression,
        autoGainControl: this.processing.autoGainControl,
        channelCount: 1,
        // ideal, not exact: if the device is gone, the browser picks the default instead of failing.
        ...(deviceId ? { deviceId: { ideal: deviceId } } : {}),
        ...(this.options.microphone || {}),
      },
    });
    const track = stream.getAudioTracks()[0];
    if (track) {
      // Unplugged (a headset turned off, say): carry on with the system default.
      track.addEventListener('ended', () => {
        if (this._stream === stream && !this._stopped)
          this._swapMicrophone('').catch((error) => this._emit('error', { error }));
      });
      const active = (track.getSettings && track.getSettings().deviceId) || '';
      this._emit('microphone', { deviceId: active, label: track.label, fallback: !!this.microphoneId && active !== this.microphoneId });
    }
    return stream;
  }

  async _swapMicrophone(deviceId) {
    const stream = await this._openMicrophone(deviceId);
    if (this._stopped || !this._ctx || !this._capture) {
      stream.getTracks().forEach((t) => t.stop());
      return;
    }
    const oldStream = this._stream;
    const oldSource = this._source;
    this._stream = stream;
    this._source = this._ctx.createMediaStreamSource(stream);
    this._source.connect(this._gain);
    if (oldSource) oldSource.disconnect();
    if (oldStream) oldStream.getTracks().forEach((t) => t.stop());
  }

  async _applySpeaker() {
    if (!this._ctx || typeof this._ctx.setSinkId !== 'function') return;
    let fallback = false;
    try {
      await this._ctx.setSinkId(this.speakerId);
    } catch {
      // Gone (unplugged) or not allowed: play to the system default.
      fallback = !!this.speakerId;
      await this._ctx.setSinkId('').catch(() => {});
    }
    this._emit('speaker', { deviceId: this.speakerId, fallback });
  }

  // ── Connection ─────────────────────────────────────────

  _connect() {
    if (this._stopped) return;
    this._setState(this._retry > 0 ? 'reconnecting' : 'connecting');
    this.bindingKey = null;
    this.boundName = null;
    this.global = null;
    const ws = new WebSocket(this.options.url);
    ws.binaryType = 'arraybuffer';
    this._ws = ws;
    ws.onopen = () => {
      const ids = loadIds();
      ws.send(JSON.stringify({ t: 'hello', codec: this.codec, user: ids.user, server: ids.server, locale: navigator.language || 'en-US' }));
      // Re-apply settings for the new session.
      if (this.muted) this._send({ t: 'mute', value: true });
      if (this.deafened) this._send({ t: 'deafen', value: true });
      if (this.volume !== 1) this._send({ t: 'volume', value: this.volume });
      if (this.sensitivity !== DEFAULT_SENSITIVITY) this._send({ t: 'sensitivity', value: this.sensitivity });
      if (this._globalTalk || !this._globalListen) this._send({ t: 'global', talk: this._globalTalk, listen: this._globalListen });
    };
    ws.onmessage = (e) => {
      if (typeof e.data === 'string') this._onControl(e.data);
      else this._onAudio(e.data);
    };
    ws.onclose = () => {
      if (this._ws !== ws) return;
      this._ws = null;
      if (this._stopped) return;
      // Reconnect with backoff: 1 s, 2 s, 4 s … up to 15 s. The binding key changes, so the UI asks again.
      const delay = Math.min(15000, 1000 * 2 ** Math.min(this._retry, 4));
      this._retry++;
      this._setState('reconnecting', this._lastReason);
      this._retryTimer = setTimeout(() => this._connect(), delay);
    };
  }

  _onControl(text) {
    let msg;
    try { msg = JSON.parse(text); } catch { return; }
    switch (msg.t) {
      case 'state':
        if (msg.state === 'connected') this._retry = 0;
        if (msg.state === 'disconnected') { this._lastReason = msg.reason; return; } // socket close follows
        this._setState(msg.state, msg.reason);
        break;
      case 'description': {
        this._emit('description', { text: msg.text });
        const linked = LINKED_TO.exec(msg.text || '');
        if (linked) { this.linkedName = linked[1]; this._emit('linked', { name: linked[1] }); }
        const key = BINDING_KEY.exec(msg.text || '');
        if (key) { this.bindingKey = key[1]; this.boundName = null; this._emit('bindingkey', { key: key[1], linkedName: linked ? linked[1] : null }); }
        const bound = BOUND_TO.exec(msg.text || '');
        if (bound) { this.boundName = bound[1]; this.bindingKey = null; this._emit('bound', { name: bound[1] }); }
        break;
      }
      case 'peers':
        this.peers = msg.list || [];
        this._emit('peers', { list: this.peers });
        break;
      case 'title':
        this._emit('title', { text: msg.text });
        break;
      case 'speaking': case 'muted': case 'deafened': case 'serverMuted': case 'serverDeafened':
        this._emit(msg.t, { value: !!msg.value });
        break;
      case 'global':
        this.global = { talk: !!msg.talk, listen: !!msg.listen };
        this._emit('global', this.global);
        break;
    }
  }

  _send(obj) {
    if (this._ws && this._ws.readyState === WebSocket.OPEN) this._ws.send(JSON.stringify(obj));
  }

  _setState(state, reason) {
    this.state = state;
    this._emit('state', { state, reason });
  }

  _emit(type, detail) {
    this.dispatchEvent(new CustomEvent(type, { detail }));
  }

  async _requestWakeLock() {
    // Keeps a phone's screen on while it's used as the "voice device" next to a console.
    try {
      if ('wakeLock' in navigator && !this._stopped && (!this._wakeLock || this._wakeLock.released))
        this._wakeLock = await navigator.wakeLock.request('screen');
    } catch { /* not allowed or unsupported */ }
  }

  // ── Audio ──────────────────────────────────────────────

  _setupCodec() {
    if (this.codec === 'opus') {
      this._makeEncoder();
      this._makeDecoder();
    } else {
      this._down = new Resampler3x();
      this._up = new Resampler3x();
    }
  }

  // A WebCodecs codec that hits an error is closed for good, so each one rebuilds itself.
  _makeEncoder() {
    this._encoder = new AudioEncoder({
      output: (chunk) => {
        const data = new Uint8Array(chunk.byteLength);
        chunk.copyTo(data);
        this._sendAudio(data);
      },
      error: (error) => {
        this._emit('error', { error });
        if (!this._stopped) setTimeout(() => this._makeEncoder(), 100);
      },
    });
    this._encoder.configure({ codec: 'opus', sampleRate: 48000, numberOfChannels: 1, bitrate: 32000, opus: { frameDuration: 20000 } });
  }

  _makeDecoder() {
    this._decoder = new AudioDecoder({
      output: (audioData) => this._onDecoded(audioData),
      error: (error) => {
        this._emit('error', { error });
        if (!this._stopped) setTimeout(() => this._makeDecoder(), 100);
      },
    });
    this._decoder.configure({ codec: 'opus', sampleRate: 48000, numberOfChannels: 2 });
  }

  _onMicFrame(frame, rms, peak) {
    this._emit('level', { rms, peak });
    if (!this._ws || this._ws.readyState !== WebSocket.OPEN) return;
    if (this.codec === 'opus') {
      if (!this._encoder || this._encoder.state !== 'configured') return;
      const audioData = new AudioData({
        format: 'f32-planar', sampleRate: 48000, numberOfFrames: FRAME, numberOfChannels: 1,
        timestamp: this._timestamp, data: frame,
      });
      this._timestamp += 20000;
      this._encoder.encode(audioData);
      audioData.close();
    } else {
      const low = new Float32Array(PCM_FRAME);
      this._down.downsample(frame, low);
      const pcm = new Int16Array(PCM_FRAME);
      for (let i = 0; i < PCM_FRAME; i++) pcm[i] = Math.max(-32768, Math.min(32767, Math.round(low[i] * 32767)));
      this._sendAudio(new Uint8Array(pcm.buffer));
    }
  }

  _sendAudio(bytes) {
    // Don't queue audio behind a slow connection; dropping a frame beats adding delay.
    if (this._ws && this._ws.readyState === WebSocket.OPEN && this._ws.bufferedAmount < 16384) {
      this._ws.send(bytes);
      this.stats.sent++;
    }
  }

  _onAudio(buffer) {
    this.stats.received++;
    if (this.codec === 'opus') {
      if (!this._decoder || this._decoder.state !== 'configured') return;
      this._decoder.decode(new EncodedAudioChunk({ type: 'key', timestamp: this._rxTimestamp = (this._rxTimestamp || 0) + 20000, data: buffer }));
    } else {
      if (buffer.byteLength !== PCM_FRAME * 2) return;
      const pcm = new Int16Array(buffer);
      const low = new Float32Array(PCM_FRAME);
      for (let i = 0; i < PCM_FRAME; i++) low[i] = pcm[i] / 32768;
      const mono = new Float32Array(FRAME);
      this._up.upsample(low, mono);
      this._play(mono, mono.slice());
    }
  }

  _onDecoded(audioData) {
    try {
      const n = audioData.numberOfFrames;
      const channels = audioData.numberOfChannels;
      const l = new Float32Array(n);
      const r = new Float32Array(n);
      try {
        audioData.copyTo(l, { planeIndex: 0, format: 'f32-planar' });
        if (channels > 1) audioData.copyTo(r, { planeIndex: 1, format: 'f32-planar' }); else r.set(l);
      } catch {
        const inter = new Float32Array(n * channels);
        audioData.copyTo(inter, { planeIndex: 0, format: 'f32' });
        for (let i = 0; i < n; i++) { l[i] = inter[i * channels]; r[i] = inter[i * channels + (channels > 1 ? 1 : 0)]; }
      }
      this._play(l, r);
    } finally {
      audioData.close();
    }
  }

  _play(l, r) {
    let sum = 0;
    for (let i = 0; i < l.length; i++) sum += l[i] * l[i];
    this.stats.playedRms = Math.sqrt(sum / l.length);
    if (this._playback) this._playback.port.postMessage({ l, r }, [l.buffer, r.buffer]);
  }
}

/** Same 3:1 windowed-sinc resampler as the bridge (Resampler3x.cs), for the PCM fallback. */
class Resampler3x {
  constructor() {
    this.taps = 48;
    this.kernel = Resampler3x.kernel || (Resampler3x.kernel = buildKernel(48, 7200 / 48000));
    this.history = new Float32Array(this.taps);
    this.pos = 0;
  }

  push(sample) {
    this.history[this.pos] = sample;
    this.pos = (this.pos + 1) % this.taps;
  }

  convolve() {
    let acc = 0;
    let idx = this.pos;
    for (let k = 0; k < this.taps; k++) {
      idx = idx === 0 ? this.taps - 1 : idx - 1;
      acc += this.kernel[k] * this.history[idx];
    }
    return acc;
  }

  downsample(input, output) {
    const n = Math.floor(input.length / 3);
    for (let i = 0; i < n; i++) {
      this.push(input[i * 3]); this.push(input[i * 3 + 1]); this.push(input[i * 3 + 2]);
      output[i] = this.convolve();
    }
    return n;
  }

  upsample(input, output) {
    for (let i = 0; i < input.length; i++) {
      for (let k = 0; k < 3; k++) {
        this.push(k === 0 ? input[i] * 3 : 0);
        output[i * 3 + k] = this.convolve();
      }
    }
    return input.length * 3;
  }
}

function buildKernel(taps, cutoff) {
  const kernel = new Float32Array(taps);
  const mid = (taps - 1) / 2;
  let sum = 0;
  for (let i = 0; i < taps; i++) {
    const x = i - mid;
    const sinc = Math.abs(x) < 1e-9 ? 2 * cutoff : Math.sin(2 * Math.PI * cutoff * x) / (Math.PI * x);
    const w = 0.42 - 0.5 * Math.cos(2 * Math.PI * i / (taps - 1)) + 0.08 * Math.cos(4 * Math.PI * i / (taps - 1));
    kernel[i] = sinc * w;
    sum += kernel[i];
  }
  for (let i = 0; i < taps; i++) kernel[i] /= sum;
  return kernel;
}

function clamp(v, lo, hi) {
  v = Number(v);
  return Number.isFinite(v) ? Math.min(hi, Math.max(lo, v)) : lo;
}

/** Stable per-browser ids, like the native app's per-install GUIDs. */
function loadIds() {
  const fresh = () => ({ user: crypto.randomUUID(), server: crypto.randomUUID() });
  try {
    const saved = JSON.parse(localStorage.getItem(ID_KEY) || 'null');
    if (saved && saved.user && saved.server) return saved;
    const ids = fresh();
    localStorage.setItem(ID_KEY, JSON.stringify(ids));
    return ids;
  } catch {
    return fresh();
  }
}
