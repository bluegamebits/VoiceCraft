// VoiceCraft web client: audio worklet processors.
// Everything on the wire is 48 kHz in 20 ms frames (960 samples). If the browser runs the
// AudioContext at another rate, these processors resample linearly at the edges.

const WIRE_RATE = 48000;
const FRAME = 960;

/** Microphone → 960-sample mono frames at 48 kHz, posted to the main thread with their RMS and peak levels. */
class VcCaptureProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.frame = new Float32Array(FRAME);
    this.pos = 0;
    this.step = sampleRate / WIRE_RATE; // input samples per output sample
    this.phase = 0;
    this.prev = 0;
  }

  process(inputs) {
    const input = inputs[0];
    if (!input || input.length === 0 || !input[0]) return true;
    const ch0 = input[0];
    if (this.step === 1) {
      for (let i = 0; i < ch0.length; i++) this.push(ch0[i]);
    } else {
      // Linear resampling to 48 kHz; phase is the fractional read position into ch0.
      while (this.phase < ch0.length) {
        const i = Math.floor(this.phase);
        const frac = this.phase - i;
        const a = i === 0 ? this.prev : ch0[i - 1];
        const b = ch0[i];
        this.push(a + (b - a) * frac);
        this.phase += this.step;
      }
      this.phase -= ch0.length;
      this.prev = ch0[ch0.length - 1];
    }
    return true;
  }

  push(sample) {
    this.frame[this.pos++] = sample;
    if (this.pos === FRAME) {
      let sum = 0;
      let peak = 0;
      for (let i = 0; i < FRAME; i++) {
        sum += this.frame[i] * this.frame[i];
        peak = Math.max(peak, Math.abs(this.frame[i]));
      }
      const out = this.frame;
      this.port.postMessage({ frame: out, rms: Math.sqrt(sum / FRAME), peak }, [out.buffer]);
      this.frame = new Float32Array(FRAME);
      this.pos = 0;
    }
  }
}

/**
 * Jitter-buffered stereo playback. Frames arrive from the network at 48 kHz; we prefill a little
 * before starting, play silence on underrun, and drop old audio if the buffer grows too large
 * (so delay never builds up).
 */
class VcPlaybackProcessor extends AudioWorkletProcessor {
  constructor() {
    super();
    this.capacity = WIRE_RATE * 2; // 2 s ring buffer
    this.left = new Float32Array(this.capacity);
    this.right = new Float32Array(this.capacity);
    this.read = 0;    // float read position (in wire samples)
    this.write = 0;   // integer write position
    this.prefill = FRAME * 3;   // 60 ms before (re)starting
    this.maxBuffered = FRAME * 10; // 200 ms: beyond this, skip ahead
    this.playing = false;
    this.step = WIRE_RATE / sampleRate; // wire samples per output sample
    this.port.onmessage = (e) => this.enqueue(e.data.l, e.data.r);
  }

  buffered() {
    return this.write - this.read;
  }

  enqueue(l, r) {
    for (let i = 0; i < l.length; i++) {
      const idx = this.write % this.capacity;
      this.left[idx] = l[i];
      this.right[idx] = r[i];
      this.write++;
    }
    if (this.buffered() > this.maxBuffered) this.read = this.write - this.prefill; // too far behind: catch up
  }

  process(_inputs, outputs) {
    const out = outputs[0];
    const outL = out[0];
    const outR = out[1] || out[0];
    if (!this.playing && this.buffered() >= this.prefill) this.playing = true;
    for (let i = 0; i < outL.length; i++) {
      if (!this.playing || this.buffered() < 2) {
        this.playing = false;
        outL[i] = 0;
        if (outR !== outL) outR[i] = 0;
        continue;
      }
      const base = Math.floor(this.read);
      const frac = this.read - base;
      const i0 = base % this.capacity;
      const i1 = (base + 1) % this.capacity;
      outL[i] = this.left[i0] + (this.left[i1] - this.left[i0]) * frac;
      if (outR !== outL) outR[i] = this.right[i0] + (this.right[i1] - this.right[i0]) * frac;
      this.read += this.step;
    }
    return true;
  }
}

registerProcessor('vc-capture', VcCaptureProcessor);
registerProcessor('vc-playback', VcPlaybackProcessor);
