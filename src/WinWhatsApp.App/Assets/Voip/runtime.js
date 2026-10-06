// The parts of WhatsApp Web's runtime that the script of its calling engine
// (wa-voip-glue.js) expects around it: Babel's helpers, the module loader
// and the message ports between the page and its workers. Loaded in the page
// and in every worker of the engine.
'use strict';

(function (g) {
  g.babelHelpers = {
    extends: Object.assign,
    inheritsLoose(subClass, superClass) {
      subClass.prototype = Object.create(superClass.prototype);
      subClass.prototype.constructor = subClass;
      Object.setPrototypeOf(subClass, superClass);
    },
    // Lets the engine's error classes extend Error and still be instances of themselves.
    wrapNativeSuper(Class) {
      function Wrapper() {
        return Reflect.construct(Class, arguments, Object.getPrototypeOf(this).constructor);
      }
      Wrapper.prototype = Object.create(Class.prototype, {
        constructor: { value: Wrapper, writable: true, configurable: true },
      });
      Object.setPrototypeOf(Wrapper, Class);
      return Wrapper;
    },
    objectWithoutPropertiesLoose(source, excluded) {
      const target = {};
      for (const key of Object.keys(source)) {
        if (!excluded.includes(key)) target[key] = source[key];
      }
      return target;
    },
    taggedTemplateLiteralLoose(strings, raw) {
      strings.raw = raw || strings.slice(0);
      return strings;
    },
  };

  // WhatsApp Web's modules are defined with __d(name, dependencies, factory)
  // and the factory gets (global, require, importDefault, importNamespace,
  // requireLazy, module, exports). All three importers can be the same here.
  const factories = new Map();
  const modules = new Map();

  g.__d = function (name, dependencies, factory) {
    factories.set(name, factory);
  };

  function require(name) {
    let module = modules.get(name);
    if (module) return module.exports;
    const factory = factories.get(name);
    if (!factory) throw new Error('voip runtime: no module ' + name);
    module = { exports: {} };
    modules.set(name, module);
    factory(g, require, require, require, undefined, module, module.exports);
    return module.exports;
  }

  function define(name, exports) {
    modules.set(name, { exports });
  }

  // Metrics and logging calls the engine's script makes along the way. Any
  // property is a function that does nothing and returns the same thing again,
  // so enums read from it work too.
  const nothing = new Proxy(function () {}, {
    get: (_, key) => (key === 'then' || typeof key === 'symbol' ? undefined : nothing),
    apply: () => nothing,
  });

  // Passes messages between the page and a worker. Every message has a type,
  // and listeners are registered per type. The engine's script also posts
  // messages with the plain postMessage of the worker, which arrive here too.
  class MessagePort {
    constructor(target) {
      this.target = target;
      this.listeners = new Map();
      this.onerror = null;
      target.addEventListener('message', (e) => this.dispatch(e.data));
      if (typeof Worker !== 'undefined' && target instanceof Worker) {
        target.addEventListener('error', (e) => this.onerror && this.onerror(e));
      }
    }

    dispatch(message) {
      if (message == null || typeof message !== 'object') return;
      const listeners = this.listeners.get(message.type);
      if (!listeners) return;
      for (const listener of [...listeners]) listener(message);
    }

    postMessage(message, transfer) {
      this.target.postMessage(message, transfer || []);
    }

    addMessageListener(type, listener) {
      let listeners = this.listeners.get(type);
      if (!listeners) this.listeners.set(type, (listeners = new Set()));
      listeners.add(listener);
    }

    removeMessageListener(type, listener) {
      this.listeners.get(type)?.delete(listener);
    }

    removeAllMessageListeners(type) {
      this.listeners.delete(type);
    }

    terminate() {
      if (typeof this.target.terminate === 'function') this.target.terminate();
    }
  }

  // Set by the page before it loads the engine: where the binary and the worker script are.
  g.voipUrls = g.voipUrls || { wasm: 'wa-voip.wasm', worker: 'worker.js' };

  define('Promise', Promise);
  define('WAWebCoreActionsODS', nothing);
  define('WAWebVoipQplHelpers', nothing);
  define('WAWebVoipWasmArtifactRegistry', nothing);
  define('WAWebVoipWebWasmWorkerResource', {});
  define('WorkerClient', { init() {} });
  define('WorkerMessagePort', { WorkerMessagePort: MessagePort, WorkerSyncedMessagePort: MessagePort });
  define('WorkerBundleResource', {
    createDedicatedWebWorker: () => new Worker(g.voipUrls.worker),
  });
  // Resource ids of WhatsApp's bundler. The only resource the script looks up is the binary.
  const bx = (id) => id;
  bx.getURL = () => g.voipUrls.wasm;
  define('bx', bx);

  g.voipRuntime = { require, define, MessagePort, nothing };
})(self);

// ---- Hashes for the engine ----
//
// The engine asks for HMAC-SHA256 and HKDF and needs the answer before it
// goes on, so Web Crypto, which answers later, does not do.
(function (g) {
  const K = new Uint32Array([
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
  ]);

  function sha256(data) {
    const length = data.length;
    const padded = new Uint8Array(((length + 9 + 63) >> 6) << 6);
    padded.set(data);
    padded[length] = 0x80;
    const view = new DataView(padded.buffer);
    view.setUint32(padded.length - 8, Math.floor(length / 0x20000000));
    view.setUint32(padded.length - 4, (length << 3) >>> 0);
    const h = new Uint32Array([
      0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
    ]);
    const w = new Uint32Array(64);
    for (let offset = 0; offset < padded.length; offset += 64) {
      for (let i = 0; i < 16; i++) w[i] = view.getUint32(offset + i * 4);
      for (let i = 16; i < 64; i++) {
        const a = w[i - 15], b = w[i - 2];
        const s0 = ((a >>> 7) | (a << 25)) ^ ((a >>> 18) | (a << 14)) ^ (a >>> 3);
        const s1 = ((b >>> 17) | (b << 15)) ^ ((b >>> 19) | (b << 13)) ^ (b >>> 10);
        w[i] = (w[i - 16] + s0 + w[i - 7] + s1) | 0;
      }
      let [a, b, c, d, e, f, gg, hh] = h;
      for (let i = 0; i < 64; i++) {
        const S1 = ((e >>> 6) | (e << 26)) ^ ((e >>> 11) | (e << 21)) ^ ((e >>> 25) | (e << 7));
        const ch = (e & f) ^ (~e & gg);
        const t1 = (hh + S1 + ch + K[i] + w[i]) | 0;
        const S0 = ((a >>> 2) | (a << 30)) ^ ((a >>> 13) | (a << 19)) ^ ((a >>> 22) | (a << 10));
        const maj = (a & b) ^ (a & c) ^ (b & c);
        const t2 = (S0 + maj) | 0;
        hh = gg; gg = f; f = e; e = (d + t1) | 0; d = c; c = b; b = a; a = (t1 + t2) | 0;
      }
      h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += gg; h[7] += hh;
    }
    const out = new Uint8Array(32);
    const outView = new DataView(out.buffer);
    for (let i = 0; i < 8; i++) outView.setUint32(i * 4, h[i]);
    return out;
  }

  function hmac(key, data) {
    if (key.length > 64) key = sha256(key);
    const inner = new Uint8Array(64 + data.length);
    const outer = new Uint8Array(64 + 32);
    for (let i = 0; i < 64; i++) {
      const k = key[i] || 0;
      inner[i] = k ^ 0x36;
      outer[i] = k ^ 0x5c;
    }
    inner.set(data, 64);
    outer.set(sha256(inner), 64);
    return sha256(outer);
  }

  function hkdf(key, salt, info, length) {
    const prk = hmac(salt && salt.length ? salt : new Uint8Array(32), key);
    const out = new Uint8Array(length);
    let previous = new Uint8Array(0);
    for (let i = 0, offset = 0; offset < length; i++) {
      const input = new Uint8Array(previous.length + info.length + 1);
      input.set(previous);
      input.set(info, previous.length);
      input[input.length - 1] = i + 1;
      previous = hmac(prk, input);
      out.set(previous.subarray(0, Math.min(32, length - offset)), offset);
      offset += 32;
    }
    return out;
  }

  function bytes(value) {
    if (value == null) return new Uint8Array(0);
    if (typeof value === 'string') return new TextEncoder().encode(value);
    if (value instanceof Uint8Array) return value;
    if (ArrayBuffer.isView(value)) return new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
    return new Uint8Array(value);
  }

  g.voipCrypto = {
    sha256,
    hmac,
    hkdf,
    bytes,
    // The callbacks the engine calls by these names.
    cryptoHkdfExtractWithSaltAndExpand: (p) => hkdf(bytes(p.key_), p.salt_ != null ? bytes(p.salt_) : null, bytes(p.info_), p.length),
    hmacSha256KeyGenerator: (p) => hmac(bytes(p.key_), bytes(p.data_)),
  };
})(self);
