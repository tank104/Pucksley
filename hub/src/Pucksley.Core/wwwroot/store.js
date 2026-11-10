// The page's live state: /api/status, the log, the conversation, and the SSE feed that keeps them fresh.

import { get } from './api.js';

const MAX_LOGS = 1500;
const MAX_CONVERSATION = 600;

const listeners = new Map();

export const store = {
  status: null,
  logs: [],
  conversation: [],
  live: false, // the SSE feed is connected
  reachable: true, // the last /api/status worked
  authNeeded: false,
  sections: {}, // settings sections fetched for the checklist: voice, household
};

export function on(event, fn) {
  if (!listeners.has(event)) listeners.set(event, new Set());
  listeners.get(event).add(fn);
  return () => listeners.get(event)?.delete(fn);
}

export function emit(event, data) {
  for (const fn of [...(listeners.get(event) || [])]) {
    try { fn(data); } catch (e) { console.error(`listener for ${event} failed`, e); }
  }
}

// ------------------------------------------------------------------ status

let inflight = null;
let again = false;
let statusTimer = null;

export function refreshStatus() {
  if (inflight) {
    again = true;
    return inflight;
  }
  inflight = (async () => {
    try {
      const s = await get('/api/status', { timeout: 10000 });
      store.status = s;
      setReachable(true);
      emit('status', s);
    } catch (e) {
      if (e.status === 401) return;
      setReachable(false);
    } finally {
      inflight = null;
      if (again) {
        again = false;
        refreshStatus();
      }
    }
  })();
  return inflight;
}

/** Re-read the status soon (several triggers in a burst = one request). */
export function scheduleStatus(delay = 250) {
  if (statusTimer) return;
  statusTimer = setTimeout(() => {
    statusTimer = null;
    refreshStatus();
  }, delay);
}

function setReachable(ok) {
  if (store.reachable === ok) return;
  store.reachable = ok;
  emit('reachable', ok);
}

function setLive(ok) {
  if (store.live === ok) return;
  store.live = ok;
  emit('live', ok);
}

// ------------------------------------------------------------------ settings sections (for the checklist)

export async function loadSection(name, fresh = false) {
  if (!fresh && store.sections[name]) return store.sections[name];
  try {
    const data = await get(`/api/settings/${name}`, { timeout: 10000 });
    store.sections[name] = data;
    emit('section', { name, data });
    return data;
  } catch {
    return store.sections[name] || null;
  }
}

export function setSection(name, data) {
  store.sections[name] = data;
  emit('section', { name, data });
}

// ------------------------------------------------------------------ log + conversation

function sig(ev, kind) {
  return kind === 'log' ? `${ev.level}|${ev.category}|${ev.message}` : `${ev.session}|${ev.role}|${ev.text}`;
}

function merge(history, recent, kind) {
  const out = Array.isArray(history) ? history.slice() : [];
  for (const ev of recent) {
    const s = sig(ev, kind);
    const t = Date.parse(ev.at);
    const tail = out.slice(-150);
    if (!tail.some((x) => sig(x, kind) === s && Math.abs(Date.parse(x.at) - t) < 5000)) out.push(ev);
  }
  return out;
}

let historyAt = 0;

export async function loadHistory() {
  const started = Date.now();
  historyAt = started;
  const [logs, conversation] = await Promise.allSettled([get('/api/logs', { timeout: 15000 }), get('/api/conversation', { timeout: 15000 })]);
  if (historyAt !== started) return; // a newer load won
  if (logs.status === 'fulfilled' && Array.isArray(logs.value)) {
    const recent = store.logs.filter((e) => e._rx && e._rx >= started - 2000);
    store.logs = merge(logs.value, recent, 'log').slice(-MAX_LOGS);
    emit('logs-reset', store.logs);
  }
  if (conversation.status === 'fulfilled' && Array.isArray(conversation.value)) {
    const recent = store.conversation.filter((e) => e._rx && e._rx >= started - 2000);
    store.conversation = merge(conversation.value, recent, 'conversation').slice(-MAX_CONVERSATION);
    emit('conversation-reset', store.conversation);
  }
}

function pluginIds() {
  return new Set((store.status?.plugins || []).map((p) => p.id));
}

function handle(ev) {
  if (!ev || typeof ev !== 'object') return;
  ev._rx = Date.now();
  switch (ev.type) {
    case 'log':
      store.logs.push(ev);
      if (store.logs.length > MAX_LOGS) store.logs.splice(0, store.logs.length - MAX_LOGS);
      emit('log', ev);
      // Plugin and puck news is logged; the status may have changed with it.
      if (/^(Plugins|Pucks|Puck|Hub)$/.test(ev.category || '') || pluginIds().has(ev.category)) scheduleStatus(600);
      break;
    case 'conversation':
      store.conversation.push(ev);
      if (store.conversation.length > MAX_CONVERSATION) store.conversation.splice(0, store.conversation.length - MAX_CONVERSATION);
      emit('conversation', ev);
      if (String(ev.session || '').startsWith('puck:')) scheduleStatus(300);
      break;
    case 'status':
      scheduleStatus(200);
      break;
    default:
      emit(ev.type, ev);
  }
}

// ------------------------------------------------------------------ the live feed

let es = null;
let retry = null;
let everConnected = false;
let pollTimer = null;

function connect() {
  clearTimeout(retry);
  try { es?.close(); } catch { /* ignore */ }
  if (typeof EventSource === 'undefined') return;
  es = new EventSource('/api/events');
  es.onopen = () => {
    const reconnect = everConnected;
    everConnected = true;
    setLive(true);
    refreshStatus();
    if (reconnect) loadHistory(); // we may have missed lines while away
  };
  es.onerror = () => {
    setLive(false);
    if (es && es.readyState === EventSource.CLOSED) {
      retry = setTimeout(connect, 4000);
    }
  };
  es.onmessage = (m) => {
    let ev;
    try { ev = JSON.parse(m.data); } catch { return; }
    handle(ev);
  };
}

function poll() {
  clearTimeout(pollTimer);
  const visible = document.visibilityState === 'visible';
  // Voice states and plugin statuses don't always announce themselves, so keep an eye on them.
  const ms = !visible ? 30000 : !store.reachable ? 3000 : store.live ? 4000 : 3000;
  pollTimer = setTimeout(async () => {
    if (document.visibilityState === 'visible' || !store.live) await refreshStatus();
    poll();
  }, ms);
}

export function startLive() {
  connect();
  poll();
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') {
      refreshStatus();
      if (!es || es.readyState === EventSource.CLOSED) connect();
    }
    poll();
  });
  on('reachable', (ok) => {
    if (ok && (!es || es.readyState === EventSource.CLOSED)) connect();
    poll();
  });
}

// ------------------------------------------------------------------ handy lookups

export function puckById(id) {
  return (store.status?.pucks || []).find((p) => p.id === id) || null;
}

export function pluginById(id) {
  return (store.status?.plugins || []).find((p) => p.id === id) || null;
}

export function connectedPucks() {
  return (store.status?.pucks || []).filter((p) => p.connected);
}
