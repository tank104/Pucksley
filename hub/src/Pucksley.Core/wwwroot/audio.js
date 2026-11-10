// Browser audio: record the microphone as a 16-bit PCM WAV (what the hub can read), and play WAV replies.

export function micAvailability() {
  if (!window.isSecureContext) {
    return {
      ok: false,
      reason: 'insecure',
      message: 'Browsers only allow the microphone on secure pages. On the hub’s own computer open http://localhost:' + (location.port || '80') + '/ to talk from the browser, or use a puck.',
    };
  }
  if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
    return { ok: false, reason: 'unsupported', message: 'This browser can’t record from a microphone.' };
  }
  if (!window.AudioContext && !window.webkitAudioContext) {
    return { ok: false, reason: 'unsupported', message: 'This browser can’t process audio.' };
  }
  return { ok: true, reason: null, message: '' };
}

function concat(chunks, total) {
  const out = new Float32Array(total);
  let o = 0;
  for (const c of chunks) {
    out.set(c, o);
    o += c.length;
  }
  return out;
}

function writeStr(view, offset, s) {
  for (let i = 0; i < s.length; i++) view.setUint8(offset + i, s.charCodeAt(i));
}

/** Mono float samples → a 16-bit PCM WAV blob. */
export function encodeWav(samples, rate) {
  const n = samples.length;
  const buf = new ArrayBuffer(44 + n * 2);
  const v = new DataView(buf);
  writeStr(v, 0, 'RIFF');
  v.setUint32(4, 36 + n * 2, true);
  writeStr(v, 8, 'WAVE');
  writeStr(v, 12, 'fmt ');
  v.setUint32(16, 16, true);
  v.setUint16(20, 1, true); // PCM
  v.setUint16(22, 1, true); // mono
  v.setUint32(24, rate, true);
  v.setUint32(28, rate * 2, true);
  v.setUint16(32, 2, true);
  v.setUint16(34, 16, true);
  writeStr(v, 36, 'data');
  v.setUint32(40, n * 2, true);
  let o = 44;
  for (let i = 0; i < n; i++, o += 2) {
    const s = Math.max(-1, Math.min(1, samples[i]));
    v.setInt16(o, s < 0 ? s * 0x8000 : s * 0x7fff, true);
  }
  return new Blob([buf], { type: 'audio/wav' });
}

/**
 * Starts recording. Returns { stop() → {wav, seconds, peak}, cancel(), seconds }.
 * onLevel(0..1) is called ~10× a second; onLimit() when maxSeconds is reached.
 */
export async function startRecording({ onLevel, onLimit, maxSeconds = 45 } = {}) {
  const stream = await navigator.mediaDevices.getUserMedia({
    audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true },
  });
  const AC = window.AudioContext || window.webkitAudioContext;
  const ctx = new AC();
  try { await ctx.resume(); } catch { /* ignore */ }
  const source = ctx.createMediaStreamSource(stream);
  const sink = ctx.createGain();
  sink.gain.value = 0; // keeps the graph running without playing the mic back
  sink.connect(ctx.destination);

  const chunks = [];
  let total = 0;
  let peak = 0;
  let limited = false;
  const onChunk = (f32) => {
    if (!f32 || !f32.length) return;
    chunks.push(f32);
    total += f32.length;
    let sum = 0;
    for (let i = 0; i < f32.length; i++) {
      const a = f32[i];
      sum += a * a;
      if (Math.abs(a) > peak) peak = Math.abs(a);
    }
    onLevel?.(Math.min(1, Math.sqrt(sum / f32.length) * 5));
    if (!limited && total / ctx.sampleRate >= maxSeconds) {
      limited = true;
      onLimit?.();
    }
  };

  let node;
  let flush = async () => {};
  try {
    if (!ctx.audioWorklet || typeof AudioWorkletNode === 'undefined') throw new Error('no worklet');
    await ctx.audioWorklet.addModule('recorder.js');
    node = new AudioWorkletNode(ctx, 'puck-recorder', { numberOfInputs: 1, numberOfOutputs: 1, outputChannelCount: [1] });
    let waiter = null;
    node.port.onmessage = (e) => {
      onChunk(e.data);
      if (waiter && e.data.length === 0) waiter();
    };
    flush = () => new Promise((resolve) => {
      const t = setTimeout(resolve, 250);
      waiter = () => { clearTimeout(t); resolve(); };
      node.port.postMessage('flush');
    });
  } catch {
    node = ctx.createScriptProcessor(4096, 1, 1);
    node.onaudioprocess = (e) => onChunk(new Float32Array(e.inputBuffer.getChannelData(0)));
  }
  source.connect(node);
  node.connect(sink);

  const started = performance.now();
  let finished = false;
  async function finish() {
    if (finished) return null;
    finished = true;
    await flush();
    try { source.disconnect(); } catch { /* ignore */ }
    try { node.disconnect(); } catch { /* ignore */ }
    try { sink.disconnect(); } catch { /* ignore */ }
    stream.getTracks().forEach((t) => t.stop());
    const rate = ctx.sampleRate;
    ctx.close().catch(() => {});
    return { samples: concat(chunks, total), rate };
  }

  return {
    async stop() {
      const r = await finish();
      if (!r) return null;
      return { wav: encodeWav(r.samples, r.rate), seconds: r.samples.length / r.rate, peak };
    },
    async cancel() {
      await finish();
    },
    get seconds() {
      return (performance.now() - started) / 1000;
    },
  };
}

// ------------------------------------------------------------------ playback

let playCtx = null;
let current = null;

/** Call from a click/tap so later playback is allowed (iOS and Chrome autoplay rules). */
export function unlockAudio() {
  const AC = window.AudioContext || window.webkitAudioContext;
  if (!AC) return;
  try {
    if (!playCtx) playCtx = new AC();
    if (playCtx.state === 'suspended') playCtx.resume().catch(() => {});
  } catch { /* ignore */ }
}

export function stopPlayback() {
  const c = current;
  current = null;
  if (!c) return;
  try {
    if (c.stop) c.stop();
    else c.pause();
  } catch { /* ignore */ }
}

export function isPlaying() {
  return current != null;
}

/** Plays a WAV blob. Resolves when it has finished (or was stopped). */
export async function playBlob(blob) {
  stopPlayback();
  unlockAudio();
  if (playCtx) {
    try {
      const data = await blob.arrayBuffer();
      const buffer = await new Promise((resolve, reject) => {
        const p = playCtx.decodeAudioData(data, resolve, reject);
        if (p && p.then) p.then(resolve, reject);
      });
      if (playCtx.state === 'suspended') await playCtx.resume();
      const src = playCtx.createBufferSource();
      src.buffer = buffer;
      src.connect(playCtx.destination);
      current = src;
      const ended = new Promise((resolve) => {
        src.onended = () => {
          if (current === src) current = null;
          resolve();
        };
      });
      src.start();
      return ended;
    } catch { /* fall back to an <audio> element */ }
  }
  const url = URL.createObjectURL(blob);
  const a = new Audio(url);
  current = a;
  return new Promise((resolve, reject) => {
    const done = () => {
      URL.revokeObjectURL(url);
      if (current === a) current = null;
      resolve();
    };
    a.addEventListener('ended', done);
    a.addEventListener('pause', done);
    a.play().catch((e) => {
      URL.revokeObjectURL(url);
      if (current === a) current = null;
      reject(e);
    });
  });
}

export function base64ToBlob(b64, type = 'audio/wav') {
  const bin = atob(b64);
  const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  return new Blob([bytes], { type });
}
