// A thread of the calling engine. The engine is C++ compiled with Emscripten,
// whose threads are workers sharing the engine's memory. The page starts a
// pool of them; each loads the engine's script and waits for its first job.
// This does what WhatsApp Web's own worker does.
'use strict';

importScripts('runtime.js', 'wa-voip-glue.js');

const GLUE = 'WAWebVoipWebWasmLoader_ContentAddressed_internal';
const port = new voipRuntime.MessagePort(self);

// The engine calls these from its threads. Everything is passed on to the
// page, apart from the answers it needs right away.
function forward(name, data, transfer) {
  port.postMessage(Object.assign({ type: 'waWasmWorkerCompatibleCallback', __name: name }, data), transfer);
}

function copy(value) {
  return ArrayBuffer.isView(value) ? value.slice() : value;
}

self.WhatsAppVoipWasmWorkerCompatibleCallbacks = {
  // Bytes in the engine's memory are copied: the memory is shared and changes.
  onSignalingXmpp: (p) => forward('onSignalingXmpp', { peerJid: p.peerJid, callId: p.callId, xmlPayload: copy(p.xmlPayload) }),
  onCallEvent: (p) => forward('onCallEvent', { eventType: p.eventType, userData: p.userData, eventDataJson: p.eventDataJson }),
  sendDataToRelay: (p) => {
    forward('sendDataToRelay', { data: copy(p.data), len: p.len, ip: p.ip, port: p.port });
    return p.len;
  },
  loggingCallback: (p) => forward('loggingCallback', p),
  initCaptureDriverJS: (p) => forward('initCaptureDriverJS', p),
  startCaptureJS: (p) => forward('startCaptureJS', p || {}),
  stopCaptureJS: (p) => forward('stopCaptureJS', p || {}),
  initPlaybackDriverJS: (p) => forward('initPlaybackDriverJS', p),
  startPlaybackJS: () => forward('startPlaybackJS', {}),
  stopPlaybackJS: () => forward('stopPlaybackJS', {}),
  startVideoCaptureJS: (p) => forward('startVideoCaptureJS', p),
  stopVideoCaptureJS: () => forward('stopVideoCaptureJS', {}),
  onVideoFrameWasmToJs: () => {},
  startDesktopCaptureJS: (p) => forward('startDesktopCaptureJS', p),
  stopDesktopCaptureJS: () => forward('stopDesktopCaptureJS', {}),
  dataChannelStateCallback: (p) => forward('dataChannelStateCallback', p),
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

let Module = {};
let bindingsReady = false;

function log(text) {
  forward('loggingCallback', { level: 1, message: 'voip: [worker] ' + text });
}

self.alert = (text) => postMessage({ cmd: 'alert', text: String(text), threadId: Module._pthread_self && Module._pthread_self() });
self.onunhandledrejection = (e) => {
  throw e.reason != null ? e.reason : e;
};

function waitMailbox(pthread) {
  const wait = Module.__emscripten_thread_mailbox_await;
  if (wait) wait(pthread);
}

function onCommand(message) {
  try {
    if (message.cmd === 'load') {
      // Commands that come in before the engine is loaded wait for it.
      const queued = [];
      const queue = (m) => queued.push(m);
      port.removeMessageListener('cmd', onCommand);
      port.addMessageListener('cmd', queue);
      let started = false;
      let loaded = false;
      const ready = () => {
        if (!started || !loaded) return;
        port.postMessage({ type: 'cmd', cmd: 'loaded' });
        port.removeMessageListener('cmd', queue);
        port.addMessageListener('cmd', onCommand);
        for (const m of queued) onCommand(m);
      };
      self.startWorker = (instance) => {
        Module = instance;
        started = true;
        ready();
      };
      for (const handler of message.handlers) {
        Module[handler] = (...args) => port.postMessage({ type: 'cmd', cmd: 'callHandler', callHandler: { handler, args } });
      }
      Module.wasmModule = message.wasmModule;
      Module.wasmMemory = message.wasmMemory;
      Module.buffer = message.wasmMemory.buffer;
      Module.workerID = message.workerID;
      Module.ENVIRONMENT_IS_PTHREAD = true;
      Module.onAbort = (what) => log('abort: ' + what);
      Module.instantiateWasm = (imports, receive) => {
        const module = Module.wasmModule;
        Module.wasmModule = null;
        return receive(new WebAssembly.Instance(module, imports));
      };
      const glue = voipRuntime.require(GLUE);
      Promise.resolve(glue.call(self, Module)).then(
        () => {
          loaded = true;
          ready();
        },
        (e) => log('load failed: ' + (e && e.message ? e.message : e)),
      );
    } else if (message.cmd === 'run') {
      const pthread = message.pthread_ptr;
      Module.__emscripten_thread_init(pthread, 0, 0, 1);
      waitMailbox(pthread);
      Module.establishStackSpace();
      Module.PThread.receiveObjectTransfer(message);
      Module.PThread.threadInitTLS();
      if (!bindingsReady) {
        Module.__embind_initialize_bindings();
        bindingsReady = true;
      }
      try {
        Module.invokeEntryPoint(message.start_routine, message.arg);
      } catch (e) {
        if (e !== 'unwind') throw e;
      }
    } else if (message.cmd === 'cancel') {
      if (Module._pthread_self && Module._pthread_self()) Module.__emscripten_thread_exit(-1);
    } else if (message.target === 'setimmediate') {
      // Handled by the engine's own listener.
    } else if (message.cmd === 'checkMailbox') {
      if (bindingsReady && Module.checkMailbox) Module.checkMailbox();
    } else if (message.cmd) {
      log('unknown command ' + message.cmd);
    }
  } catch (e) {
    log('error in ' + message.cmd + ': ' + (e && e.stack ? e.stack : e));
    if (Module.__emscripten_thread_crashed) Module.__emscripten_thread_crashed();
    if (!(e instanceof WebAssembly.RuntimeError)) throw e;
  }
}

port.addMessageListener('cmd', onCommand);
