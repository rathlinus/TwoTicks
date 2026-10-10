// Runs WhatsApp's calling engine for the app. The app shows this page in a
// browser nobody sees and exchanges messages with it: it hands over what
// WhatsApp's servers send about calls and gets back what the engine wants to
// send. Sound goes through the page's microphone and speakers, and the call's
// media goes to WhatsApp's relays over WebTransport, as in WhatsApp Web.
//
// On Windows the browser is WebView2 and the messages are its web messages.
// On macOS and Linux it is a browser of the system, run without a window, and
// the messages go over a WebSocket to the app, which also serves the page.
'use strict';

const GLUE = 'WAWebVoipWebWasmLoader_ContentAddressed_internal';

// Values of the engine's enums, from WhatsApp Web.
const CallEvent = {
  CallStateChanged: 16, CallEnding: 38, SelfVideoStateChanged: 51, PeerVideoStateChanged: 52, MuteStateChanged: 73,
  SpeakerStatusChanged: 100, VideoStateChanged: 146, RelayListUpdate: 156,
};
const CallState = {
  None: 0, Calling: 1, PreacceptReceived: 2, ReceivedCall: 3, AcceptSent: 4, AcceptReceived: 5, CallActive: 6,
  CallActiveElseWhere: 7, ReceivedCallWithoutOffer: 8, Rejoining: 9, Link: 10, ConnectedLonely: 11, PreCalling: 12, CallStateEnding: 13,
};
const EndCallReason = { Timeout: 1, Self: 2 };
const VideoState = {
  Disabled: 0, Enabled: 1, Paused: 2, UpgradeRequest: 3, UpgradeAccept: 4, UpgradeReject: 5, Stopped: 6,
  UpgradeRejectByTimeout: 7, UpgradeCancel: 8, UpgradeCancelByTimeout: 9, UnknownPeer: 10, UpgradeRequestV2: 11, Error: 20,
};
// Events too frequent to log.
const QUIET_EVENTS = new Set([CallEvent.SpeakerStatusChanged, 75, 117, 123]);
// How long a call rings before it gives up, as in WhatsApp Web.
const CALLER_TIMEOUT = 90000;
const CALLEE_TIMEOUT = 60000;

let engine = null;
let starting = null;
let debug = false;
// The user parts of this account's phone number and LID.
let ownUsers = new Set();

function userOf(jid) {
  return String(jid || '').split('@')[0].split(':')[0];
}

// ---- Talking to the app ----

const webview = window.chrome && window.chrome.webview;
// The key the app put into the page's address, which opens the WebSocket.
const channelKey = new URLSearchParams(location.search).get('key');
let socket = null;

function post(message) {
  if (webview) webview.postMessage(message);
  else if (socket && socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
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

// ---- Health ----
//
// A call once went quiet halfway through: the page stopped doing anything and
// its log showed nothing about why. So the page tells the app every second
// that it is alive, and the app logs when that stops. The page logs calls into
// the engine that hold up its thread, what goes in and out during a call, and
// what the browser does to it: freezing it, sound devices, relays closing.

const BEAT = 1000;
// How long a call into the engine may hold up this thread before it is logged.
const SLOW = 100;
const STATS_EVERY = 10000;

const health = {
  slow: new Map(), // name -> { count, worst }, since the last beat
  counts: null,
  countingSince: 0,
  lastBeat: performance.now(),
};

function resetCounts(now) {
  health.counts = { relayIn: 0, relayOut: 0, relayDropped: 0, mic: 0, speaker: 0, camera: 0, pictures: 0, signalsIn: 0, signalsOut: 0 };
  health.countingSince = now;
}
resetCounts(performance.now());

// Runs a call into the engine and notes when it holds up the thread.
function timed(name, fn) {
  const start = performance.now();
  try {
    return fn();
  } finally {
    const took = performance.now() - start;
    if (took > SLOW) {
      const slow = health.slow.get(name) || { count: 0, worst: 0 };
      slow.count++;
      slow.worst = Math.max(slow.worst, took);
      health.slow.set(name, slow);
    }
  }
}

function audioState(side) {
  if (!side) return 'off';
  return side.context ? side.context.state : 'starting';
}

function logStats(now) {
  const c = health.counts;
  const seconds = Math.round((now - health.countingSince) / 1000);
  log(2, 'voip: last ' + seconds + ' s: relays ' + c.relayIn + ' in, ' + c.relayOut + ' out, ' + c.relayDropped + ' dropped, ' +
    sessions.size + ' open; microphone ' + c.mic + ' chunks, ' + audioState(audio.capture) + '; speaker ' + c.speaker +
    ' chunks, ' + audioState(audio.playback) + '; camera ' + c.camera + ' frames, other side ' + c.pictures +
    ' pictures; signaling ' + c.signalsIn + ' in, ' + c.signalsOut + ' out');
  resetCounts(now);
}

function beat() {
  const now = performance.now();
  const late = now - health.lastBeat - BEAT;
  health.lastBeat = now;
  if (late > 500) log(2, "voip: the page's thread was held up for " + Math.round(late) + ' ms');
  if (health.slow.size) {
    const list = [...health.slow].map(([name, s]) => name + (s.count > 1 ? ' ' + s.count + ' times, up to ' : ' ') + Math.round(s.worst) + ' ms');
    log(2, "voip: slow on the page's thread: " + list.join(', '));
    health.slow.clear();
  }
  post({ type: 'alive' });
  if (!currentCall) resetCounts(now);
  else if (now - health.countingSince >= STATS_EVERY) logStats(now);
}

setInterval(beat, BEAT);

document.addEventListener('freeze', () => log(2, 'voip: the browser froze the page'));
document.addEventListener('resume', () => log(2, 'voip: the browser resumed the page'));
document.addEventListener('visibilitychange', () => log(2, 'voip: the page is ' + document.visibilityState));
window.addEventListener('pagehide', () => log(2, 'voip: the page is going away'));
window.addEventListener('error', (e) => log(1, 'voip: error on the page: ' + (e.error ? describe(e.error) : e.message + ' at ' + e.filename + ':' + e.lineno)));
window.addEventListener('unhandledrejection', (e) => log(1, 'voip: unhandled on the page: ' + describe(e.reason)));
if (navigator.mediaDevices) navigator.mediaDevices.addEventListener('devicechange', () => log(2, 'voip: the sound devices changed'));
self.voipWorkerError = (e) => log(1, 'voip: a thread of the engine failed: ' + (e.message || 'unknown') + (e.filename ? ' at ' + e.filename + ':' + e.lineno : ''));

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
      if (!session.closed) log(2, 'voip: relay ' + relay.domain + ' ended the session');
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
  health.counts.relayIn++;
  timed('relay packet', () => engine.handleOnMessageFromHeap(ptr, bytes.byteLength, session.ip, session.port));
}

function sendToRelay(ip, port, data) {
  const relay = relays.get(addressKey(ip, port));
  if (!relay || !validDomain(relay.domain)) {
    health.counts.relayDropped++;
    return;
  }
  let session = sessions.get(relay.domain);
  if (!session) {
    if (sessions.size >= MAX_SESSIONS * 2) {
      health.counts.relayDropped++;
      return;
    }
    session = openSession(relay);
  }
  session.ip = ip;
  session.port = port;
  // A copy: the engine's memory is shared and changes under the write.
  const copy = new Uint8Array(data);
  if (session.writer) {
    health.counts.relayOut++;
    session.writer.write(copy).catch(() => health.counts.relayDropped++);
  } else if (session.queue.length < MAX_QUEUE) {
    health.counts.relayOut++;
    session.queue.push(copy);
  } else {
    health.counts.relayDropped++;
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

// The microphone, speaker and camera picked in the app's settings, by their
// names in Windows, which the browser uses as the labels of the devices; null
// for the Windows default.
const chosen = { microphone: null, speaker: null, camera: null };

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
  const stream = await navigator.mediaDevices.getUserMedia({ audio });
  for (const track of stream.getAudioTracks()) {
    // Not raised when the page stops the track itself.
    track.addEventListener('ended', () => log(2, 'voip: the microphone ' + track.label + ' went away'));
    track.addEventListener('mute', () => log(2, 'voip: the microphone ' + track.label + ' gives no sound'));
    track.addEventListener('unmute', () => log(2, 'voip: the microphone ' + track.label + ' gives sound again'));
  }
  return stream;
}

// Logs what the browser does to the sound of a call, such as suspending it.
function watchAudio(context, name, isCurrent) {
  context.addEventListener('statechange', () => {
    if (isCurrent()) log(2, 'voip: the ' + name + ' is ' + context.state);
  });
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
  watchAudio(context, 'microphone', () => audio.capture === capture);
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
        health.counts.mic++;
        timed('microphone chunk', () => engine.onAudioDataFromJs(ptr, capture.pending.length));
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
  watchAudio(context, 'speaker', () => audio.playback === playback);
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
    health.counts.speaker++;
    timed('speaker chunk', () => engine.requestAudioDataFromWasmVoip(ptr, bytes));
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

// The engine asks to set up the microphone and to start it right after,
// without waiting; setting up waits for the browser. Each side's steps run
// in order, one after the other, as in WhatsApp Web.
const queues = { capture: Promise.resolve(), playback: Promise.resolve(), video: Promise.resolve() };

function run(queue, name, step) {
  queues[queue] = queues[queue].then(step).catch((e) => {
    log(1, 'voip: ' + name + ' failed: ' + describe(e));
    if (queue === 'capture') post({ type: 'micFailed', message: String(e && e.message ? e.message : e) });
  });
  return queues[queue];
}

// ---- Video ----
//
// The engine encodes and decodes the video itself, as in WhatsApp Web. The page
// hands it the camera's pictures as NV12, at the size it asks for, and gets the
// other side's pictures back as NV12, I420 or RGBA. The page is never shown,
// so both go to the app as JPEG, and the call window draws them.

const VideoFormat = { NV12: 0, I420: 1, RGBA: 3 };
const FRAME_FORMATS = { [VideoFormat.NV12]: 'NV12', [VideoFormat.I420]: 'I420', [VideoFormat.RGBA]: 'RGBA' };
const Orientation = { Normal: 1, Rotate90: 2, Rotate180: 3, Rotate270: 4 };
// The longest side of the pictures the app gets, and how often it gets its own.
const PICTURE_SIZE = { self: 320, peer: 960 };
const SELF_PICTURE_EVERY = 66;

const video = {
  capture: null, // { stream, width, height, fps, canvas, context, nv12, buffer, last, stopped, reader, element, timer }
  callIsVideo: false,
  selfState: null,
  // Per picture the app gets: a canvas to draw it on, and whether one is on its way.
  pictures: { self: { canvas: null, busy: false, last: 0 }, peer: { canvas: null, busy: false, last: 0 } },
};

async function startVideoCapture(p) {
  stopVideoCapture();
  const width = p.width || 640;
  const height = p.height || 480;
  const fps = p.max_fps || 15;
  const stream = await openCamera({ width: { ideal: width }, height: { ideal: height }, frameRate: { ideal: fps } });
  if (!stream) {
    // As WhatsApp Web does: without a camera the call goes on with the video off.
    if (engine) engine.setCallVideoMute(true);
    return;
  }
  const canvas = new OffscreenCanvas(width, height);
  const capture = {
    stream, width, height, fps, canvas, context: canvas.getContext('2d', { willReadFrequently: true }),
    nv12: new Uint8Array(width * height + 2 * Math.ceil(width / 2) * Math.ceil(height / 2)),
    buffer: heapBuffer(), last: 0, stopped: false, reader: null, element: null, timer: null,
  };
  video.capture = capture;
  const track = stream.getVideoTracks()[0];
  log(2, 'voip: camera ' + track.label + ' at ' + width + 'x' + height + ', ' + fps + ' fps');
  track.addEventListener('ended', () => {
    if (video.capture === capture) log(2, 'voip: the camera ' + track.label + ' went away');
  });
  if (typeof MediaStreamTrackProcessor === 'function') readCamera(capture, track);
  else await pollCamera(capture);
}

// The camera picked in the settings, or the default one; when that does not
// start, each other camera in turn, virtual ones too. A virtual camera often
// gives no picture until the program behind it sends one, and the browser
// gives up on it after a while.
async function openCamera(constraints) {
  const tried = [];
  const attempt = async (id, name) => {
    try {
      return await navigator.mediaDevices.getUserMedia({ video: id ? Object.assign({ deviceId: { exact: id } }, constraints) : constraints });
    } catch (e) {
      log(1, 'voip: the camera ' + name + ' did not start: ' + describe(e));
      tried.push(name + ' (' + (e && e.message ? e.message : e) + ')');
      return null;
    }
  };
  let chosenId = null;
  try {
    chosenId = await findDevice('videoinput', chosen.camera);
  } catch (e) {
    log(2, 'voip: looking for the chosen camera failed: ' + describe(e));
  }
  let stream = await attempt(chosenId, chosen.camera || 'default');
  if (stream) return stream;
  let cameras = [];
  try {
    cameras = (await navigator.mediaDevices.enumerateDevices()).filter((d) => d.kind === 'videoinput' && d.deviceId !== chosenId);
  } catch (e) {
    log(2, 'voip: listing the cameras failed: ' + describe(e));
  }
  for (const camera of cameras) {
    stream = await attempt(camera.deviceId, camera.label || camera.deviceId.slice(0, 8));
    if (stream) return stream;
  }
  post({ type: 'cameraFailed', message: tried.length ? tried.join(', ') : 'no camera' });
  return null;
}

// Takes the camera's pictures as the browser has them, where it can hand them over.
async function readCamera(capture, track) {
  const reader = new MediaStreamTrackProcessor({ track }).readable.getReader();
  capture.reader = reader;
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      try {
        if (!capture.stopped) onCameraFrame(capture, value, value.displayWidth, value.displayHeight);
      } finally {
        value.close();
      }
      if (capture.stopped) break;
    }
  } catch (e) {
    if (!capture.stopped) log(1, 'voip: reading the camera failed: ' + describe(e));
  }
}

// The same through a video element, for browsers without MediaStreamTrackProcessor.
async function pollCamera(capture) {
  const element = document.createElement('video');
  element.muted = true;
  element.playsInline = true;
  element.srcObject = capture.stream;
  capture.element = element;
  await element.play();
  const tick = () => {
    if (capture.stopped) return;
    if (element.videoWidth) onCameraFrame(capture, element, element.videoWidth, element.videoHeight);
    capture.timer = setTimeout(tick, 1000 / capture.fps);
  };
  tick();
}

function onCameraFrame(capture, source, sourceWidth, sourceHeight) {
  const now = performance.now();
  if (now - capture.last < 1000 / capture.fps - 2 || !engine || !sourceWidth || !sourceHeight) return;
  capture.last = now;
  const { width, height, context } = capture;
  // Filled to the size the engine asked for, cut at the sides as needed.
  const scale = Math.max(width / sourceWidth, height / sourceHeight);
  const w = sourceWidth * scale;
  const h = sourceHeight * scale;
  context.drawImage(source, (width - w) / 2, (height - h) / 2, w, h);
  const nv12 = toNV12(context.getImageData(0, 0, width, height).data, width, height, capture.nv12);
  const ptr = ensureHeap(capture.buffer, nv12.length);
  if (!ptr) return;
  engine.GROWABLE_HEAP_U8().set(nv12, ptr);
  health.counts.camera++;
  timed('camera frame', () => engine.onVideoDataFromJs(ptr, nv12.length, width, height, capture.fps, VideoFormat.NV12, Orientation.Normal));
  if (now - video.pictures.self.last >= SELF_PICTURE_EVERY) postPicture('self', capture.canvas, width, height, Orientation.Normal);
}

// RGBA to NV12 with the BT.601 coefficients WhatsApp Web uses: the full
// brightness plane, then the colour at half the size, U and V interleaved.
function toNV12(rgba, width, height, out) {
  for (let i = 0, j = 0, n = width * height; i < n; i++, j += 4) {
    out[i] = (16 + ((66 * rgba[j] + 129 * rgba[j + 1] + 25 * rgba[j + 2] + 128) >> 8));
  }
  let k = width * height;
  for (let y = 0; y < height; y += 2) {
    for (let x = 0; x < width; x += 2) {
      const a = (y * width + x) * 4;
      const b = x + 1 < width ? a + 4 : a;
      const c = y + 1 < height ? a + width * 4 : a;
      const d = x + 1 < width ? c + 4 : c;
      const r = (rgba[a] + rgba[b] + rgba[c] + rgba[d]) >> 2;
      const g = (rgba[a + 1] + rgba[b + 1] + rgba[c + 1] + rgba[d + 1]) >> 2;
      const bl = (rgba[a + 2] + rgba[b + 2] + rgba[c + 2] + rgba[d + 2]) >> 2;
      out[k++] = Math.max(0, Math.min(255, ((-38 * r - 74 * g + 112 * bl + 128) >> 8) + 128));
      out[k++] = Math.max(0, Math.min(255, ((112 * r - 94 * g - 18 * bl + 128) >> 8) + 128));
    }
  }
  return out;
}

function stopVideoCapture() {
  const capture = video.capture;
  video.capture = null;
  if (!capture) return;
  capture.stopped = true;
  if (capture.reader) capture.reader.cancel().catch(() => {});
  if (capture.timer) clearTimeout(capture.timer);
  if (capture.element) capture.element.srcObject = null;
  for (const track of capture.stream.getTracks()) track.stop();
  freeHeap(capture.buffer);
  post({ type: 'picture', who: 'self', jpeg: '' });
}

// A decoded picture of the other side.
function onPeerFrame(p) {
  if (p.userJid === 'selfPreviewJid' || video.pictures.peer.busy) return;
  const format = FRAME_FORMATS[p.format];
  if (!format) {
    log(2, 'voip: a picture in format ' + p.format + ', which the page cannot show');
    return;
  }
  health.counts.pictures++;
  let frame;
  try {
    frame = new VideoFrame(new Uint8Array(p.frameBuffer), { format, codedWidth: p.width, codedHeight: p.height, timestamp: 0 });
    postPicture('peer', frame, p.width, p.height, p.orientation || Orientation.Normal);
  } catch (e) {
    log(1, 'voip: showing a picture failed: ' + describe(e));
  } finally {
    if (frame) frame.close();
  }
}

// Draws a picture upright and small enough, and sends it to the app as JPEG.
// The drawing is done before this returns, so the source may be closed then.
function postPicture(who, source, sourceWidth, sourceHeight, orientation) {
  const picture = video.pictures[who];
  if (picture.busy) return;
  const sideways = orientation === Orientation.Rotate90 || orientation === Orientation.Rotate270;
  const uprightWidth = sideways ? sourceHeight : sourceWidth;
  const uprightHeight = sideways ? sourceWidth : sourceHeight;
  const scale = Math.min(1, PICTURE_SIZE[who] / Math.max(uprightWidth, uprightHeight));
  const width = Math.max(1, Math.round(uprightWidth * scale));
  const height = Math.max(1, Math.round(uprightHeight * scale));
  if (!picture.canvas || picture.canvas.width !== width || picture.canvas.height !== height) picture.canvas = new OffscreenCanvas(width, height);
  const canvas = picture.canvas;
  const context = canvas.getContext('2d');
  context.save();
  context.translate(width / 2, height / 2);
  context.rotate((Math.PI * ((orientation || 1) - 1)) / 2);
  const w = sideways ? height : width;
  const h = sideways ? width : height;
  context.drawImage(source, -w / 2, -h / 2, w, h);
  context.restore();
  picture.busy = true;
  picture.last = performance.now();
  canvas.convertToBlob({ type: 'image/jpeg', quality: 0.8 })
    .then((blob) => blob.arrayBuffer())
    .then((data) => post({ type: 'picture', who, width, height, jpeg: base64(new Uint8Array(data)) }))
    .catch((e) => log(1, 'voip: encoding a picture failed: ' + describe(e)))
    .finally(() => {
      picture.busy = false;
    });
}

function onVideoState(json) {
  const data = JSON.parse(json);
  // Some events say whose video it is only by the JID, as WhatsApp Web reads them.
  const self = data.is_self != null ? data.is_self === true : ownUsers.has(userOf(data.jid && data.jid.raw_jid));
  if (self) video.selfState = data.video_state;
  post({ type: 'video', self, state: data.video_state });
}

// Turns the camera on or off as WhatsApp Web does: in a voice call, turning it
// on asks the other side to switch to video.
function setCamera(on) {
  const s = video.selfState;
  const inactive = s == null || s === VideoState.Disabled || s === VideoState.Error || s === VideoState.Stopped ||
    s === VideoState.UpgradeCancel || s === VideoState.UpgradeCancelByTimeout || s === VideoState.UpgradeReject ||
    s === VideoState.UpgradeRejectByTimeout;
  const upgrade = on && !video.callIsVideo && inactive;
  const status = upgrade ? engine.requestVideoUpgrade() : engine.setCallVideoMute(!on);
  if (status !== 0) {
    log(1, 'voip: ' + (upgrade ? 'asking for video' : 'turning the camera ' + (on ? 'on' : 'off')) + ' failed with ' + status);
    post({ type: 'error', request: 'camera', message: 'status ' + status });
  }
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
          timed('endCall', () => engine.endCall(EndCallReason.Timeout, true));
        } catch (e) {
          log(1, 'voip: ending the call failed: ' + describe(e));
        }
      }, outgoing ? CALLER_TIMEOUT : CALLEE_TIMEOUT);
    }
  } else {
    clearRingTimer();
  }
  video.callIsVideo = !terminal && info.video_enabled === true;
  if (terminal) {
    closeRelays();
    stopVideoCapture();
    video.selfState = null;
  }

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
    else if (type === CallEvent.SelfVideoStateChanged || type === CallEvent.PeerVideoStateChanged || type === CallEvent.VideoStateChanged) onVideoState(json);
    else if (type === CallEvent.CallEnding) post({ type: 'ending', data: JSON.parse(json) });
  } catch (e) {
    log(1, 'voip: handling event ' + type + ' failed: ' + describe(e));
  }
}

const callbacks = {
  onSignalingXmpp: (p) => {
    try {
      health.counts.signalsOut++;
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
    run('capture', 'initCapture', () => initCapture(p));
  },
  startCaptureJS: (p) => {
    if (p && p.device_type === 1) return;
    run('capture', 'startCapture', startCapture);
  },
  stopCaptureJS: (p) => {
    if (p && p.device_type === 1) return;
    run('capture', 'stopCapture', stopCapture);
  },
  initPlaybackDriverJS: (p) => run('playback', 'initPlayback', () => initPlayback(p)),
  startPlaybackJS: () => run('playback', 'startPlayback', startPlayback),
  stopPlaybackJS: () => run('playback', 'stopPlayback', stopPlayback),
  startVideoCaptureJS: (p) => run('video', 'startVideoCapture', () => startVideoCapture(p || {})),
  stopVideoCaptureJS: () => run('video', 'stopVideoCapture', stopVideoCapture),
  onVideoFrameWasmToJs: onPeerFrame,
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
  ownUsers = new Set([m.pn, m.pnUser, m.lid].map(userOf).filter((u) => u.length > 0));
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
      engine.startVoipCall(m.peer, devices, m.callId, m.video === true, m.peerPn, false, token);
    } finally {
      devices.delete();
      token.delete();
    }
  },
  accept(m) {
    // With the microphone on, and the camera as the person chose.
    engine.acceptCall(true, m.video === true);
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
  camera(m) {
    setCamera(m.on === true);
  },
  acceptVideo(m) {
    const status = engine.acceptPeerVideo(m.peer);
    if (status !== 0) log(1, 'voip: switching to video failed with ' + status);
  },
  async devices(m) {
    chosen.microphone = m.microphone || null;
    chosen.speaker = m.speaker || null;
    chosen.camera = m.camera || null;
    await run('capture', 'applyDevices', applyDevices);
  },
};

function receive(m) {
  const handler = m && handlers[m.type];
  if (!handler) return;
  if (m.type !== 'init' && !engine) {
    log(2, 'voip: ' + m.type + ' before the engine started');
    return;
  }
  if (m.type === 'offer' || m.type === 'message' || m.type === 'receipt' || m.type === 'ack') health.counts.signalsIn++;
  Promise.resolve()
    .then(() => timed(m.type, () => handler(m)))
    .catch((err) => {
      log(1, 'voip: ' + m.type + ' failed: ' + describe(err));
      if (m.type === 'init') post({ type: 'failed', message: String(err && err.message ? err.message : err) });
      else post({ type: 'error', request: m.type, message: String(err && err.message ? err.message : err) });
    });
}

if (webview) {
  webview.addEventListener('message', (e) => receive(e.data));
  post({ type: 'loaded', isolated: self.crossOriginIsolated === true });
} else if (channelKey) {
  socket = new WebSocket('ws://' + location.host + '/channel?key=' + encodeURIComponent(channelKey));
  socket.addEventListener('open', () => post({ type: 'loaded', isolated: self.crossOriginIsolated === true }));
  socket.addEventListener('message', (e) => receive(JSON.parse(e.data)));
  // The app is gone, and with it the reason for this page.
  socket.addEventListener('close', () => window.close());
}
