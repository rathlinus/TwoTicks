// The audio threads of a call. Capture hands the microphone's samples to the
// page, which passes them to the engine; playback plays what the page fetches
// from the engine, asking for more before it runs out.
'use strict';

class VoipCapture extends AudioWorkletProcessor {
  process(inputs) {
    const input = inputs[0];
    if (input && input[0]) this.port.postMessage(input[0].slice(0));
    return true;
  }
}

class VoipPlayback extends AudioWorkletProcessor {
  constructor(options) {
    super();
    const { channels, chunk } = options.processorOptions;
    this.channels = channels;
    // Samples per request, interleaved when there is more than one channel.
    this.chunk = chunk * channels;
    this.buffer = new Float32Array(this.chunk * 16);
    this.read = 0;
    this.length = 0;
    this.requested = 0;
    this.port.onmessage = (e) => this.push(e.data);
  }

  push(samples) {
    this.requested = Math.max(0, this.requested - 1);
    const size = this.buffer.length;
    for (let i = 0; i < samples.length; i++) {
      if (this.length === size) {
        // Too far behind: drop the oldest sample.
        this.read = (this.read + 1) % size;
        this.length--;
      }
      this.buffer[(this.read + this.length) % size] = samples[i];
      this.length++;
    }
  }

  process(inputs, outputs) {
    const output = outputs[0];
    const frames = output[0].length;
    const size = this.buffer.length;
    for (let i = 0; i < frames; i++) {
      for (let c = 0; c < this.channels; c++) {
        let sample = 0;
        if (this.length > 0) {
          sample = this.buffer[this.read];
          this.read = (this.read + 1) % size;
          this.length--;
        }
        if (output[c]) output[c][i] = sample;
      }
    }
    // Keep about three requests' worth ahead.
    while (this.length + this.requested * this.chunk < this.chunk * 3 && this.requested < 4) {
      this.requested++;
      this.port.postMessage(0);
    }
    return true;
  }
}

registerProcessor('voip-capture', VoipCapture);
registerProcessor('voip-playback', VoipPlayback);
