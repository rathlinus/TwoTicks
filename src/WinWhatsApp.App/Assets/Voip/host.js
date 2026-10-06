// Runs WhatsApp's calling engine for the app. The app shows this page in a
// hidden WebView2 and talks to it with web messages: it hands over what
// WhatsApp's servers send about calls and gets back what the engine wants to
// send. Sound goes through the page's microphone and speakers, and the call's
// media goes to WhatsApp's relays over WebTransport, as in WhatsApp Web.
'use strict';

const GLUE = 'WAWebVoipWebWasmLoader_ContentAddressed_internal';

// Values of the engine's enums, from WhatsApp Web.
const CallEvent = { CallStateChanged: 16, CallEnding: 38, MuteStateChanged: 73, SpeakerStatusChanged: 100, RelayListUpdate: 156 };
const CallState = {
  None: 0, Calling: 1, PreacceptReceived: 2, ReceivedCall: 3, AcceptSent: 4, AcceptReceived: 5, CallActive: 6,
  CallActiveElseWhere: 7, ReceivedCallWithoutOffer: 8, Rejoining: 9, Link: 10, ConnectedLonely: 11, PreCalling: 12, CallStateEnding: 13,
};
const EndCallReason = { Timeout: 1, Self: 2 };
// Events too frequent to log.
const QUIET_EVENTS = new Set([CallEvent.SpeakerStatusChanged, 75, 117, 123]);
// How long a call rings before it gives up, as in WhatsApp Web.
const CALLER_TIMEOUT = 90000;
const CALLEE_TIMEOUT = 60000;

let engine = null;
let starting = null;
let debug = false;

// ---- Talking to the app ----

const webview = window.chrome && window.chrome.webview;

function post(message) {
  if (webview) webview.postMessage(message);
  else console.log('voip ->', message);
}

function log(level, message) {
  // 1 error, 2 warning, 3 info, 4 and 5 debug.
  if (level <= 2 || (debug && level <= 3)) post({ type: 'log', level, message: String(message) });
}

function describe(e) {
  return e && e.stack ? e.stack : String(e);
}

function base64(bytes) {
  let s = '';
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  return btoa(s);
}

function unbase64(text) {
  if (!text) return new Uint8Array(0);
  const s = atob(text);
  const bytes = new Uint8Array(s.length);
  for (let i = 0; i < s.length; i++) bytes[i] = s.charCodeAt(i);
  return bytes;
}

function toBytes(value) {
  if (value instanceof Uint8Array) return value;
  if (ArrayBuffer.isView(value)) return new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
  if (value instanceof ArrayBuffer || (typeof SharedArrayBuffer !== 'undefined' && value instanceof SharedArrayBuffer)) return new Uint8Array(value);
  if (Array.isArray(value)) return Uint8Array.from(value);
  throw new Error('unexpected bytes of type ' + typeof value);
}

// The engine's byte lists, for privacy tokens.
function byteList(text) {
  const list = new engine.Uint8List();
  for (const b of unbase64(text)) list.push_back(b);
  return list;
}

// ---- Memory in the engine ----

function heapBuffer() {
  return { ptr: 0, size: 0 };
}

function ensureHeap(buffer, size) {
  if (buffer.size < size) {
    if (buffer.ptr) engine._free(buffer.ptr);
    buffer.ptr = engine._malloc(size);
    buffer.size = buffer.ptr ? size : 0;
  }
  return buffer.ptr;
}

function freeHeap(buffer) {
  if (buffer.ptr && engine) engine._free(buffer.ptr);
  buffer.ptr = 0;
  buffer.size = 0;
}

// ---- WebTransport to the relays ----
//
// The engine sends its packets to relays by address. The relay list event
// says which relay has which address, and which host name and token to open
// a WebTransport session with. One session carries everything for a relay,
// whichever of its addresses the engine sends to; what comes back is handed
// to the engine as coming from the address it last sent to.

const relays = new Map(); // "ip:port" -> relay
const sessions = new Map(); // host name -> session
const MAX_SESSIONS = 3;
const MAX_QUEUE = 64;
const receiveBuffer = heapBuffer();

function addressKey(ip, port) {
  return ip.includes(':') ? '[' + ip + ']:' + port : ip + ':' + port;
}

function validDomain(domain) {
  return typeof domain === 'string' && (domain.endsWith('.whatsapp.com') || /^wt\.[a-z0-9-]+\.fna\.whatsapp\.net$/i.test(domain));
}

function onRelayList(json) {
  const list = JSON.parse(json);
  const tokens = list.relay_tokens || [];
  const authTokens = list.auth_tokens || [];
  relays.clear();
  const withIPv4 = new Set();
  const entries = [];
  for (const relay of list.relays || []) {
    if (relay.token_id == null || relay.token_id < 0 || relay.token_id >= tokens.length) continue;
    const auth = relay.auth_token_id != null && relay.auth_token_id >= 0 && relay.auth_token_id < authTokens.length
      ? authTokens[relay.auth_token_id] : null;
    for (const address of relay.addresses || []) {
      if (address.protocol !== 0) continue;
      const base = { relayId: relay.relay_id, name: relay.relay_name, domain: relay.domain_name, token: tokens[relay.token_id], auth };
      if (address.ipv4 && address.port != null) {
        entries.push(Object.assign({ ip: address.ipv4, port: address.port, ipv6: false }, base));
        withIPv4.add(relay.relay_id);
      }
      if (address.ipv6 && address.port_v6 != null) {
        entries.push(Object.assign({ ip: address.ipv6, port: address.port_v6, ipv6: true }, base));
      }
    }
  }
  for (const entry of entries) {
    // A relay that has an IPv4 address is reached over that.
    if (entry.ipv6 && withIPv4.has(entry.relayId)) continue;
    relays.set(addressKey(entry.ip, entry.port), entry);
  }
  log(3, 'voip: relays ' + [...relays.values()].map((r) => r.name + '@' + r.domain).join(', '));

  let opened = sessions.size;
  for (const relay of relays.values()) {
    if (opened >= MAX_SESSIONS) break;
    if (validDomain(relay.domain) && !sessions.has(relay.domain)) {
      openSession(relay);
      opened++;
    }
  }
}

function openSession(relay) {
  const session = { domain: relay.domain, ip: relay.ip, port: relay.port, writer: null, queue: [], transport: null, closed: false };
  sessions.set(relay.domain, session);
  const params = new URLSearchParams();
  params.set('token', relay.token);
  if (relay.auth != null) params.set('auth', relay.auth);
  const url = 'https://' + relay.domain + '/webtransport?' + params.toString();
  (async () => {
    try {
      const transport = new WebTransport(url);
      session.transport = transport;
      await transport.ready;
      if (session.closed) {
        transport.close();
        return;
      }
      session.writer = transport.datagrams.writable.getWriter();
      log(3, 'voip: connected to relay ' + relay.domain);
      for (const data of session.queue.splice(0)) session.writer.write(data).catch(() => {});
      const reader = transport.datagrams.readable.getReader();
      for (;;) {
        const { done, value } = await reader.read();
        if (done || session.closed) break;
        if (value) deliver(session, value);
      }
    } catch (e) {
      if (!session.closed) log(2, 'voip: relay ' + relay.domain + ' failed: ' + describe(e));
    } finally {
      if (sessions.get(relay.domain) === session) sessions.delete(relay.domain);
    }
  })();
  return session;
}

function deliver(session, bytes) {
  if (!engine) return;
  const ptr = ensureHeap(receiveBuffer, bytes.byteLength);
  if (!ptr) return;
  engine.GROWABLE_HEAP_U8().set(bytes, ptr);
  engine.handleOnMessageFromHeap(ptr, bytes.byteLength, session.ip, session.port);
}

function sendToRelay(ip, port, data) {
  const relay = relays.get(addressKey(ip, port));
  if (!relay || !validDomain(relay.domain)) return;
  let session = sessions.get(relay.domain);
  if (!session) {
    if (sessions.size >= MAX_SESSIONS * 2) return;
    session = openSession(relay);
  }
  session.ip = ip;
  session.port = port;
  // A copy: the engine's memory is shared and changes under the write.
  const copy = new Uint8Array(data);
  if (session.writer) {
    session.writer.write(copy).catch(() => {});
  } else if (session.queue.length < MAX_QUEUE) {
    session.queue.push(copy);
  }
}

function closeRelays() {
  for (const session of sessions.values()) {
    session.closed = true;
    try {
      if (session.transport) session.transport.close();
    } catch (e) {
      // Already closed.
    }
  }
  sessions.clear();
  relays.clear();
}

// ---- Sound ----
//
// The engine asks for a microphone and speakers with the sample rate it wants.
// Both run in audio contexts at that rate, so the browser does the resampling.

const audio = {
  capture: null, // { params, stream, context, node, buffer, pending, filled }
  playback: null, // { params, context, node, element, buffer }
};
let workletUrl = 'audio-worklet.js';

// The microphone and speaker picked in the app's settings, by their names in
// Windows, which the browser uses as the labels of the devices; null for the
// Windows default.
const chosen = { microphone: null, speaker: null };

// The browser's ID of the device with that name, or null for the default.
async function findDevice(kind, name) {
  if (!name) return null;
  let devices = await navigator.mediaDevices.enumerateDevices();
  if (!devices.some((d) => d.label)) {
    // The names show only once the page may use the microphone.
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    for (const track of stream.getTracks()) track.stop();
    devices = await navigator.mediaDevices.enumerateDevices();
  }
  const ofKind = devices.filter((d) => d.kind === kind && d.deviceId !== 'default' && d.deviceId !== 'communications');
  const found = ofKind.find((d) => d.label === name) || ofKind.find((d) => d.label.includes(name));
  if (!found) log(2, 'voip: no ' + kind + ' named ' + name + ', using the default');
  return found ? found.deviceId : null;
}

async function openMicrophone() {
  const id = await findDevice('audioinput', chosen.microphone);
  const audio = { echoCancellation: true, noiseSuppression: true, autoGainControl: true, channelCount: 1 };
  if (id) audio.deviceId = { exact: id };
  return navigator.mediaDevices.getUserMedia({ audio });
}

async function applySpeaker(context) {
  if (typeof context.setSinkId !== 'function') return;
  const id = await findDevice('audiooutput', chosen.speaker);
  await context.setSinkId(id || '');
}

// Moves a call that runs to the devices now picked.
async function applyDevices() {
  const capture = audio.capture;
  if (capture && capture.context && capture.node) {
    const stream = await openMicrophone();
    if (audio.capture !== capture) {
      for (const track of stream.getTracks()) track.stop();
      return;
    }
    if (capture.source) capture.source.disconnect();
    for (const track of capture.stream.getTracks()) track.stop();
    capture.stream = stream;
    capture.source = capture.context.createMediaStreamSource(stream);
    capture.source.connect(capture.node);
  }
  const playback = audio.playback;
  if (playback && playback.context) await applySpeaker(playback.context);
}

async function initCapture(p) {
  stopCapture();
  const params = { rate: p.sample_rate || 16000, channels: p.channels || 1, chunk: p.frames_per_chunk || 320 };
  const stream = await openMicrophone();
  audio.capture = { params, stream, context: null, node: null, buffer: heapBuffer(), pending: new Float32Array(params.chunk * params.channels), filled: 0 };
}

async function startCapture() {
  const capture = audio.capture;
  if (!capture || capture.context) return;
  const context = new AudioContext({ sampleRate: capture.params.rate, latencyHint: 'interactive' });
  capture.context = context;
  await context.audioWorklet.addModule(workletUrl);
  if (audio.capture !== capture) return context.close();
  const source = context.createMediaStreamSource(capture.stream);
  const node = new AudioWorkletNode(context, 'voip-capture', { numberOfInputs: 1, numberOfOutputs: 0 });
  node.port.onmessage = (e) => onCaptured(capture, e.data);
  source.connect(node);
  capture.source = source;
  capture.node = node;
  if (context.state === 'suspended') await context.resume();
}

function onCaptured(capture, samples) {
  if (audio.capture !== capture || !engine) return;
  const { channels } = capture.params;
  for (let i = 0; i < samples.length; i++) {
    for (let c = 0; c < channels; c++) capture.pending[capture.filled++] = samples[i];
    if (capture.filled === capture.pending.length) {
      const ptr = ensureHeap(capture.buffer, capture.pending.length * 4);
      if (ptr) {
        engine.GROWABLE_HEAP_F32().set(capture.pending, ptr >> 2);
        engine.onAudioDataFromJs(ptr, capture.pending.length);
      }
      capture.filled = 0;
    }
  }
}

function stopCapture() {
  const capture = audio.capture;
  audio.capture = null;
  if (!capture) return;
  for (const track of capture.stream.getTracks()) track.stop();
  if (capture.node) capture.node.disconnect();
  if (capture.context) capture.context.close().catch(() => {});
  freeHeap(capture.buffer);
}

function initPlayback(p) {
  stopPlayback();
  audio.playback = {
    params: { rate: p.sample_rate || 16000, channels: p.channels || 1, chunk: p.frames_per_chunk || 320 },
    context: null, node: null, element: null, buffer: heapBuffer(),
  };
}

async function startPlayback() {
  const playback = audio.playback;
  if (!playback || playback.context) return;
  const { rate, channels, chunk } = playback.params;
  const context = new AudioContext({ sampleRate: rate, latencyHint: 'interactive' });
  playback.context = context;
  await context.audioWorklet.addModule(workletUrl);
  if (audio.playback !== playback) return context.close();
  const node = new AudioWorkletNode(context, 'voip-playback', {
    numberOfInputs: 0, numberOfOutputs: 1, outputChannelCount: [channels], processorOptions: { channels, chunk },
  });
  node.port.onmessage = () => {
    if (audio.playback !== playback || !engine) return;
    const bytes = chunk * channels * 4;
    const ptr = ensureHeap(playback.buffer, bytes);
    if (!ptr) return;
    engine.requestAudioDataFromWasmVoip(ptr, bytes);
    node.port.postMessage(engine.GROWABLE_HEAP_F32().slice(ptr >> 2, (ptr >> 2) + bytes / 4));
  };
  node.connect(context.destination);
  playback.node = node;
  try {
    await applySpeaker(context);
  } catch (e) {
    log(2, 'voip: could not use the chosen speaker: ' + describe(e));
  }
  if (context.state === 'suspended') await context.resume();
}

function stopPlayback() {
  const playback = audio.playback;
  audio.playback = null;
  if (!playback) return;
  if (playback.node) playback.node.disconnect();
  if (playback.context) playback.context.close().catch(() => {});
  freeHeap(playback.buffer);
}

function run(name, promise) {
  Promise.resolve(promise).catch((e) => {
    log(1, 'voip: ' + name + ' failed: ' + describe(e));
    if (name === 'initCapture' || name === 'startCapture') post({ type: 'micFailed', message: String(e && e.message ? e.message : e) });
  });
}

// ---- The engine's callbacks ----

let currentCall = null; // { id, state, isCaller }
let ringTimer = null;

function clearRingTimer() {
  if (ringTimer) clearTimeout(ringTimer);
  ringTimer = null;
}

function onCallStateChanged(json) {
  const data = JSON.parse(json);
  const info = data.call_info || {};
  const state = info.call_state != null ? info.call_state : CallState.None;
  const terminal = state === CallState.None || state === CallState.CallStateEnding || state === CallState.CallActiveElseWhere;
  const previous = currentCall;
  currentCall = terminal ? null : { id: info.call_id, state, isCaller: info.is_caller === true };

  const outgoing = (state === CallState.Calling || state === CallState.PreacceptReceived || state === CallState.PreCalling) && info.is_caller === true;
  const ringing = state === CallState.ReceivedCall && info.is_caller !== true;
  if (outgoing || ringing) {
    if (!ringTimer || !previous || previous.id !== info.call_id) {
      clearRingTimer();
      ringTimer = setTimeout(() => {
        ringTimer = null;
        log(3, 'voip: nobody answered');
        try {
          engine.endCall(EndCallReason.Timeout, true);
        } catch (e) {
          log(1, 'voip: ending the call failed: ' + describe(e));
        }
      }, outgoing ? CALLER_TIMEOUT : CALLEE_TIMEOUT);
    }
  } else {
    clearRingTimer();
  }
  if (terminal) closeRelays();

  post({
    type: 'state',
    state,
    callId: info.call_id || '',
    peer: (info.peer_jid && info.peer_jid.raw_jid) || '',
    isCaller: info.is_caller === true,
    video: info.video_enabled === true,
    activeSeconds: info.call_active_duration || 0,
  });
}

function onCallEvent(p) {
  const type = p.eventType;
  const json = p.eventDataJson;
  if (!QUIET_EVENTS.has(type)) log(3, 'voip: event ' + type + (debug && json ? ' ' + String(json).slice(0, 400) : ''));
  try {
    if (type === CallEvent.CallStateChanged) onCallStateChanged(json);
    else if (type === CallEvent.RelayListUpdate) onRelayList(json);
    else if (type === CallEvent.CallEnding) post({ type: 'ending', data: JSON.parse(json) });
  } catch (e) {
    log(1, 'voip: handling event ' + type + ' failed: ' + describe(e));
  }
}

const callbacks = {
  onSignalingXmpp: (p) => {
    try {
      post({ type: 'signal', peer: p.peerJid, callId: p.callId, payload: base64(toBytes(p.xmlPayload)) });
    } catch (e) {
      log(1, 'voip: sending signaling failed: ' + describe(e));
    }
  },
  onCallEvent,
  sendDataToRelay: (p) => {
    sendToRelay(p.ip, p.port, toBytes(p.data).subarray(0, p.len));
    return p.len;
  },
  loggingCallback: (p) => log(p.level, p.message),
  initCaptureDriverJS: (p) => {
    // 1 is the computer's own sound, for sharing the screen.
    if (p && p.device_type === 1) return;
    run('initCapture', initCapture(p));
  },
  startCaptureJS: (p) => {
    if (p && p.device_type === 1) return;
    run('startCapture', startCapture());
  },
  stopCaptureJS: (p) => {
    if (p && p.device_type === 1) return;
    stopCapture();
  },
  initPlaybackDriverJS: (p) => initPlayback(p),
  startPlaybackJS: () => run('startPlayback', startPlayback()),
  stopPlaybackJS: () => stopPlayback(),
  // Video is not supported: the engine is told there is no camera.
  startVideoCaptureJS: () => {},
  stopVideoCaptureJS: () => {},
  onVideoFrameWasmToJs: () => {},
  startDesktopCaptureJS: () => {},
  stopDesktopCaptureJS: () => {},
  dataChannelStateCallback: () => {},
  cryptoHkdfExtractWithSaltAndExpand: voipCrypto.cryptoHkdfExtractWithSaltAndExpand,
  hmacSha256KeyGenerator: voipCrypto.hmacSha256KeyGenerator,
  // Calls from people who are not contacts are still allowed to ring.
  isParticipantKnownContact: () => true,
  // The browser cancels echo, suppresses noise and controls the gain (all three bits).
  getBrowserAudioProcessingStatus: () => 7,
  getPersistentDirectoryPath: () => '',
  getBweModelPath: () => null,
  getMLModelPathForType: () => null,
  videoFrameConsumed: () => {},
  videoCaptureFrameTick: () => {},
  videoEncodedFrameTick: () => {},
  videoCaptureFpsReset: () => {},
};

window.WhatsAppVoipWasmWorkerCompatibleCallbacks = callbacks;
window.WhatsAppVoipWasmCallbacks = {
  onVoipReady: () => {},
  onCallEvent,
  initCaptureDriverJS: callbacks.initCaptureDriverJS,
  startCaptureJS: callbacks.startCaptureJS,
  stopCaptureJS: callbacks.stopCaptureJS,
};

// ---- Starting the engine ----

async function startEngine(m) {
  debug = m.debug === true;
  if (m.wasm) voipUrls.wasm = m.wasm;
  const load = voipRuntime.require(GLUE);
  const module = await load({
    pthreadPoolSizeOverride: 20,
    print: (text) => log(3, 'voip: ' + text),
    printErr: (text) => log(2, 'voip: ' + text),
    onAbort: (what) => post({ type: 'failed', message: 'The calling engine stopped: ' + what }),
  });
  if (typeof module.initVoipLogging === 'function') module.initVoipLogging();
  if (m.countryCode && typeof module.setABPropString === 'function') module.setABPropString('self_country_code', m.countryCode);
  module.initVoipStack(m.pn, m.pnUser, m.lid);
  // Calls go through WhatsApp's relays only, never straight to the other side.
  if (typeof module.setHideMyIp === 'function') module.setHideMyIp(true);
  engine = module;
}

// ---- What the app asks for ----

const handlers = {
  async init(m) {
    if (!starting) starting = startEngine(m);
    await starting;
    post({ type: 'ready' });
  },
  offer(m) {
    const token = byteList(m.tcToken);
    try {
      engine.handleIncomingSignalingOffer(m.node, m.platform || '', m.version || '0', String(m.e || 0), String(m.t || 0),
        m.offline === true, m.notContact === true, m.peer, token);
    } finally {
      token.delete();
    }
  },
  message(m) {
    const token = byteList(m.tcToken);
    try {
      engine.handleIncomingSignalingMessage(m.node, m.platform || '', m.version || '0', String(m.e || 0), String(m.t || 0),
        m.offline === true, m.peer, token);
    } finally {
      token.delete();
    }
  },
  receipt(m) {
    const token = byteList(m.tcToken);
    try {
      engine.handleIncomingSignalingReceipt(m.node, m.peer, token);
    } finally {
      token.delete();
    }
  },
  ack(m) {
    const token = byteList(m.tcToken);
    try {
      engine.handleIncomingSignalingAck(m.node, m.error || '0', m.ackType || '', m.peer, token);
    } finally {
      token.delete();
    }
  },
  call(m) {
    const devices = new engine.StringList();
    const token = byteList(m.tcToken);
    try {
      for (const device of m.devices) devices.push_back(device);
      engine.startVoipCall(m.peer, devices, m.callId, false, m.peerPn, false, token);
    } finally {
      devices.delete();
      token.delete();
    }
  },
  accept() {
    engine.acceptCall(true, false);
  },
  reject() {
    engine.rejectCall();
  },
  end() {
    engine.endCall(EndCallReason.Self, true);
  },
  mute(m) {
    engine.setCallMute(m.muted === true);
  },
  async devices(m) {
    chosen.microphone = m.microphone || null;
    chosen.speaker = m.speaker || null;
    await applyDevices();
  },
};

if (webview) {
  webview.addEventListener('message', (e) => {
    const m = e.data;
    const handler = m && handlers[m.type];
    if (!handler) return;
    if (m.type !== 'init' && !engine) {
      log(2, 'voip: ' + m.type + ' before the engine started');
      return;
    }
    Promise.resolve()
      .then(() => handler(m))
      .catch((err) => {
        log(1, 'voip: ' + m.type + ' failed: ' + describe(err));
        if (m.type === 'init') post({ type: 'failed', message: String(err && err.message ? err.message : err) });
        else post({ type: 'error', request: m.type, message: String(err && err.message ? err.message : err) });
      });
  });
  post({ type: 'loaded', isolated: self.crossOriginIsolated === true });
}
