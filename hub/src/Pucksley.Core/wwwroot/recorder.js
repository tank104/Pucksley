// AudioWorklet: hands the microphone's samples (mono float32) to the page in ~4096-sample chunks.
class PuckRecorder extends AudioWorkletProcessor {
  constructor() {
    super();
    this.buf = new Float32Array(4096);
    this.n = 0;
    this.port.onmessage = (e) => {
      if (e.data === 'flush') this.flush(true);
    };
  }

  flush(final) {
    if (this.n > 0) {
      this.port.postMessage(this.buf.slice(0, this.n));
      this.n = 0;
    }
    if (final) this.port.postMessage(new Float32Array(0)); // "that was everything"
  }

  process(inputs) {
    const input = inputs[0];
    if (input && input.length > 0) {
      const channels = input.length;
      const len = input[0].length;
      for (let i = 0; i < len; i++) {
        let v = input[0][i];
        if (channels > 1) {
          let sum = 0;
          for (let c = 0; c < channels; c++) sum += input[c][i];
          v = sum / channels;
        }
        this.buf[this.n++] = v;
        if (this.n === this.buf.length) this.flush(false);
      }
    }
    return true;
  }
}

registerProcessor('puck-recorder', PuckRecorder);
