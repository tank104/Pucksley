// Pieces several pages share: the puck orb, puck facts, pending approvals, the chat transcript,
// plugin tiles and badges, and the "connect a puck" steps.

import { h, clear, fmtAgo, fmtClock, openDialog, toast, markdown, capitalize, copyButton, spinner, uid, parseTime } from './dom.js';
import { icon, pluginIconName } from './icons.js';
import { post, enc } from './api.js';
import { store, refreshStatus, puckById } from './store.js';

// ------------------------------------------------------------------ pucks

export function puckState(p) {
  if (!p || !p.connected) return { state: 'offline', label: 'Offline' };
  switch (p.voice) {
    case 'listening': return { state: 'listening', label: 'Listening…' };
    case 'thinking': return { state: 'thinking', label: 'Thinking…' };
    case 'speaking': return { state: 'speaking', label: 'Speaking…' };
    default: return { state: 'idle', label: 'Ready' };
  }
}

/** The round puck: a dial ring around a dark screen that shows what the assistant is doing. */
export function puckOrb(state = 'idle', size = 56, label = '') {
  return h('div', { class: 'orb', style: { '--size': `${size}px` }, dataset: { state }, role: 'img', 'aria-label': label || state },
    h('div', { class: 'orb-screen' },
      h('div', { class: 'orb-glow' }),
      h('div', { class: 'orb-arc' }),
      h('div', { class: 'orb-bars' }, h('i'), h('i'), h('i'), h('i'), h('i'))));
}

export function setOrb(el, state, label) {
  if (el.dataset.state !== state) el.dataset.state = state;
  el.setAttribute('aria-label', label || state);
}

export function signalInfo(rssi) {
  if (rssi == null) return null;
  const bars = rssi >= -55 ? 4 : rssi >= -65 ? 3 : rssi >= -75 ? 2 : 1;
  return { bars, label: ['', 'weak', 'fair', 'good', 'excellent'][bars] };
}

export function signalBars(bars) {
  return h('span', { class: `sig sig-${bars}`, 'aria-hidden': 'true' }, h('i'), h('i'), h('i'), h('i'));
}

export function batteryText(p) {
  if (p.battery == null || p.battery < 0) return p.usbPower ? 'USB power (no battery)' : '—';
  return `${p.battery}%${p.charging ? ', charging' : p.usbPower ? ', on USB power' : ''}`;
}

export function batteryIcon(p) {
  if (p.battery == null || p.battery < 0) return null;
  const level = Math.max(0, Math.min(100, p.battery));
  const low = level <= 15 && !p.charging;
  return h('span', { class: ['batt', low && 'batt-low', p.charging && 'batt-chg'], 'aria-hidden': 'true' },
    h('span', { class: 'batt-fill', style: { width: `${level}%` } }));
}

const FACES = {
  assistant: 'Assistant', home: 'Home (now playing)', controls: 'Controls', timer: 'Timer', status: 'Status',
  confirm: 'A yes/no question', setup: 'Wi-Fi setup', picker: 'Picker', spin: 'Spinner', dice: 'Dice', pong: 'Pong',
};

export function faceName(face) {
  if (!face) return '—';
  return FACES[face] || capitalize(face);
}

export function transportLabel(t) {
  return t === 'wifi' ? 'Wi-Fi' : t === 'usb' ? 'USB' : t;
}

export function transportBadges(p) {
  if (!p.connected) return [h('span', { class: 'badge badge-muted' }, 'Offline')];
  return (p.transports || []).map((t) => h('span', { class: 'badge badge-conn' }, icon(t === 'usb' ? 'usb' : 'wifi', { size: 13 }), transportLabel(t)));
}

/** "::ffff:192.168.1.50:55827" → "192.168.1.50" */
export function prettyAddress(addr) {
  let a = String(addr || '').trim();
  if (!a) return '';
  a = a.replace(/^::ffff:/i, '');
  const m = /^(\d{1,3}(?:\.\d{1,3}){3})(?::\d+)?$/.exec(a);
  return m ? m[1] : a;
}

/** The address a puck should use to reach this hub. */
export function puckHubUrl() {
  const lan = store.status?.hub?.lanUrl;
  if (!lan) return '';
  try {
    const u = new URL(lan);
    return `ws://${u.host}/puck`;
  } catch {
    return '';
  }
}

/** A compact puck tile (Home). */
export function puckTile(p, { onClick } = {}) {
  const st = puckState(p);
  const sig = signalInfo(p.rssi);
  const el = h('a', { class: ['puck-tile', !p.connected && 'is-offline'], href: `#/pucks/${enc(p.id)}`, onClick },
    puckOrb(st.state, 48, st.label),
    h('div', { class: 'puck-tile-text' },
      h('div', { class: 'puck-tile-name' }, p.name || p.deviceName || p.id),
      h('div', { class: 'puck-tile-sub' }, [p.room, p.connected ? st.label : `Offline · seen ${fmtAgo(p.lastSeen)}`].filter(Boolean).join(' · '))),
    h('div', { class: 'puck-tile-meta' },
      p.connected && sig ? signalBars(sig.bars) : null,
      p.connected ? batteryIcon(p) : null));
  return el;
}

// ------------------------------------------------------------------ pending approvals

function roomSuggestions() {
  const set = new Set();
  for (const p of store.status?.pucks || []) if (p.room) set.add(p.room);
  for (const m of store.status?.mediaSources || []) if (m.name) set.add(m.name);
  ['Kitchen', 'Lounge', 'Living room', 'Bedroom', 'Office', 'Dining room'].forEach((r) => set.add(r));
  return [...set];
}

export async function approvePuck(p) {
  const listId = uid('rooms');
  const name = h('input', { class: 'input', id: uid('name'), placeholder: 'Kitchen puck', autocomplete: 'off', value: '' });
  const room = h('input', { class: 'input', id: uid('room'), placeholder: 'Kitchen', autocomplete: 'off', list: listId });
  const datalist = h('datalist', { id: listId }, roomSuggestions().map((r) => h('option', { value: r })));
  const body = h('div', { class: 'approve' },
    h('p', null, 'Check that the puck’s screen shows this code:'),
    h('div', { class: 'code-big', 'aria-label': `Code ${String(p.code).split('').join(' ')}` }, String(p.code).split('').map((d) => h('span', null, d))),
    h('p', { class: 'muted small' }, `${p.name}${p.address ? ` at ${prettyAddress(p.address)}` : ''}. If the code doesn’t match, choose Reject.`),
    h('div', { class: 'field' }, h('label', { class: 'field-label', for: name.id }, 'Name'), name, h('p', { class: 'field-help' }, 'What you’ll call it, e.g. Kitchen puck.')),
    h('div', { class: 'field' }, h('label', { class: 'field-label', for: room.id }, 'Room'), room, datalist,
      h('p', { class: 'field-help' }, 'The room it lives in. “Turn the music down” then means this room’s speaker.')));
  const result = await openDialog({
    title: `Approve ${p.name}?`,
    body,
    onOpen: () => setTimeout(() => name.focus(), 30),
    actions: [
      { label: 'Reject', value: 'reject', danger: true },
      { label: 'Not now', value: null },
      {
        label: 'Approve',
        primary: true,
        run: async () => {
          await post(`/api/pucks/${enc(p.id)}/approve`, { name: name.value.trim() || null, room: room.value.trim() || null });
          return 'approved';
        },
      },
    ],
  });
  if (result === 'approved') {
    toast(`${name.value.trim() || p.name} is approved and connected.`, 'ok');
    refreshStatus();
  } else if (result === 'reject') {
    await rejectPuck(p);
  }
}

export async function rejectPuck(p) {
  try {
    await post(`/api/pucks/${enc(p.id)}/reject`);
    toast(`Turned ${p.name} away. If it keeps trying it will ask again in about 30 seconds.`);
  } catch (e) {
    toast(e.message, 'error');
  }
  refreshStatus();
}

/** The banner for pucks waiting to be approved. Returns an element (empty when none). */
export function pendingBanner(pending) {
  const wrap = h('div', { class: 'pending-wrap' });
  for (const p of pending || []) {
    wrap.append(h('div', { class: 'pending', role: 'region', 'aria-label': `${p.name} wants to join` },
      h('div', { class: 'pending-orb' }, puckOrb('waiting', 52, 'Waiting for approval')),
      h('div', { class: 'pending-text' },
        h('div', { class: 'pending-title' }, `${p.name} wants to join this hub`),
        h('div', { class: 'pending-sub' }, 'Make sure its screen shows ', h('strong', { class: 'code-inline' }, p.code), p.address ? ` · ${prettyAddress(p.address)}` : '')),
      h('div', { class: 'pending-actions' },
        h('button', { type: 'button', class: 'btn btn-ghost', onClick: () => rejectPuck(p) }, 'Reject'),
        h('button', { type: 'button', class: 'btn btn-primary', onClick: () => approvePuck(p) }, icon('check', { size: 16 }), 'Approve…'))));
  }
  return wrap;
}

// ------------------------------------------------------------------ connecting a puck

export function howToConnect({ compact = false } = {}) {
  const url = puckHubUrl();
  return h('div', { class: ['howto', compact && 'howto-compact'] },
    h('ol', { class: 'steps' },
      h('li', null, h('strong', null, 'Flash the Pucksley firmware'), ' onto the puck (the ESP-IDF project in the ', h('code', null, 'firmware'), ' folder: ', h('code', null, 'idf.py -p COM5 flash'), ').'),
      h('li', null, h('strong', null, 'Get it onto your Wi-Fi'), ', one of two ways:',
        h('ul', null,
          h('li', null, h('strong', null, 'USB: '), 'plug it into the computer running this hub with a USB data cable. It appears on the ', h('a', { href: '#/pucks' }, 'Pucks'), ' page straight away; use ', h('em', null, 'Wi-Fi setup over USB'), ' on its card.'),
          h('li', null, h('strong', null, 'Its own Wi-Fi: '), 'on your phone, join the network called ', h('code', null, 'Puck-XXXX'), ', open ', h('code', null, 'http://192.168.4.1'), ' and enter your Wi-Fi name and password.'))),
      h('li', null, h('strong', null, 'Approve it here'), '. A puck that joins over Wi-Fi shows a 3-digit code; approve it when the same code appears on this page.')),
    url ? h('div', { class: 'howto-url' },
      h('span', { class: 'muted' }, 'If the puck can’t find the hub by itself, give it this hub address:'),
      h('code', { class: 'url-code' }, url), copyButton(url)) : null);
}

// ------------------------------------------------------------------ plugins

export function levelClass(level) {
  switch (String(level || '').toLowerCase()) {
    case 'ok': return 'ok';
    case 'warning': return 'warn';
    case 'error': return 'err';
    default: return 'idle';
  }
}

export function statusDot(level) {
  return h('span', { class: `dot ${levelClass(level)}`, 'aria-hidden': 'true' });
}

const KINDS = { 'speech-to-text': ['Speech to text', 'ear'], voice: ['Voice', 'speaker'], media: ['Media', 'music'] };

export function kindBadge(kind) {
  const [label, ico] = KINDS[kind] || [capitalize(kind), 'tool'];
  return h('span', { class: 'badge badge-kind' }, icon(ico, { size: 13 }), label);
}

export function pluginTile(iconName, size = 40) {
  return h('span', { class: 'ptile', style: { '--tile': `${size}px` } }, icon(pluginIconName(iconName), { size: Math.round(size * 0.5) }));
}

export function needsAttention(p) {
  if (!p.enabled) return false;
  const l = levelClass(p.status?.level);
  return l === 'warn' || l === 'err';
}

/** The plugin that offers a speech engine (the API doesn't say, so match by id and name). */
export function pluginForEngine(engine, kind) {
  if (!engine) return null;
  const want = kind === 'stt' ? 'speech-to-text' : 'voice';
  const plugins = (store.status?.plugins || []).filter((p) => (p.kinds || []).includes(want));
  const id = String(engine.id || '').toLowerCase();
  const name = String(engine.name || '').toLowerCase();
  const score = (p) => {
    const pid = p.id.toLowerCase();
    const pname = String(p.name || '').toLowerCase();
    if (pid === id) return 5;
    if (pname === name) return 4;
    if (id.startsWith(pid) || pid.startsWith(id)) return 3;
    if (name && (name.startsWith(pname) || pname.startsWith(name))) return 2;
    if (name && pname && (name.includes(pname) || pname.includes(name))) return 1;
    return 0;
  };
  let best = null;
  let bestScore = 0;
  for (const p of plugins) {
    const s = score(p);
    if (s > bestScore) { best = p; bestScore = s; }
  }
  return best || (plugins.length === 1 ? plugins[0] : null);
}

// ------------------------------------------------------------------ the chat transcript

export function sessionInfo(ev) {
  const s = ev.session || '';
  if (s.startsWith('puck:')) {
    const p = puckById(s.slice(5));
    return { label: p?.name || ev.who || 'A puck', icon: 'puck', kind: 'puck' };
  }
  if (s === 'web:voice') return { label: 'Voice, from the browser', icon: 'mic', kind: 'web' };
  if (s === 'web:test') return { label: 'Claude test', icon: 'sparkle', kind: 'web' };
  if (s.startsWith('web:')) return { label: 'Typed in the browser', icon: 'talk', kind: 'web' };
  if (s === 'notify') return { label: 'Notification', icon: 'bell', kind: 'notify' };
  return { label: ev.who || s || 'Hub', icon: 'info', kind: 'other' };
}

export function prettyTool(name) {
  if (!name) return 'Tool';
  if (name === 'web_search') return 'Web search';
  if (name === 'web_fetch') return 'Read a web page';
  let best = null;
  for (const p of store.status?.plugins || []) {
    const pre = `${p.id.replace(/-/g, '_')}_`;
    if (name.startsWith(pre) && (!best || pre.length > best.pre.length)) best = { p, pre };
  }
  if (!best && name.startsWith('mcp_')) {
    const rest = name.slice(4).replace(/_/g, ' ');
    return `MCP · ${rest}`;
  }
  const rest = best ? name.slice(best.pre.length) : name;
  const verb = capitalize(rest.replace(/_/g, ' '));
  return best ? `${best.p.name} · ${verb}` : verb;
}

function toolIcon(name) {
  if (name === 'web_search' || name === 'web_fetch') return 'search';
  let best = null;
  for (const p of store.status?.plugins || []) {
    const pre = `${p.id.replace(/-/g, '_')}_`;
    if (name.startsWith(pre) && (!best || pre.length > best.pre.length)) best = { p, pre };
  }
  return best ? pluginIconName(best.p.icon) : 'tool';
}

function parseTool(text) {
  const s = String(text || '');
  const i = s.indexOf(' ');
  if (i < 0) return { name: s, args: '', raw: '' };
  const name = s.slice(0, i);
  const raw = s.slice(i + 1).trim();
  let args = raw;
  try {
    const o = JSON.parse(raw);
    if (o && typeof o === 'object') {
      args = Object.entries(o).map(([k, v]) => `${k}: ${typeof v === 'object' ? JSON.stringify(v) : v}`).join(', ');
    }
  } catch { /* leave as is */ }
  if (args.length > 140) args = `${args.slice(0, 137)}…`;
  return { name, args, raw };
}

const norm = (t) => String(t ?? '').trim();

/**
 * The transcript. add(ev) appends one conversation line (deduplicating a line this page already
 * showed itself). Options: compact, scroller (element that scrolls), emptyText.
 */
export function createChatView({ compact = false, scroller = null, emptyText = 'Nothing has been said yet.', filter = null } = {}) {
  const list = h('div', { class: ['chat', compact && 'chat-compact'], role: 'log', 'aria-live': 'polite', 'aria-relevant': 'additions' });
  const empty = h('div', { class: 'chat-empty' }, emptyText);
  const root = h('div', { class: 'chat-root' }, empty, list);
  let all = []; // {ev, local}
  let rendered = []; // entries currently in the DOM
  let lastSession = null;
  let lastAt = 0;
  const openTools = new Map();
  let currentFilter = filter;

  const scrollEl = () => scroller || null;
  function nearBottom() {
    const s = scrollEl();
    if (!s) return true;
    return s.scrollHeight - s.scrollTop - s.clientHeight < 140;
  }
  function scrollToEnd() {
    const s = scrollEl();
    if (s) s.scrollTop = s.scrollHeight;
  }

  function meta(ev) {
    const info = sessionInfo(ev);
    return h('div', { class: 'chat-meta' }, icon(info.icon, { size: 14 }), h('span', null, info.label), h('span', { class: 'chat-time' }, fmtClock(ev.at)));
  }

  function errorExtras(text) {
    const t = String(text || '').toLowerCase();
    if (t.includes('api key')) return h('a', { class: 'btn btn-sm', href: '#/assistant' }, icon('key', { size: 15 }), 'Add the API key');
    if (t.includes('voice page') || t.includes('speech recognition')) return h('a', { class: 'btn btn-sm', href: '#/voice' }, icon('voice', { size: 15 }), 'Open Voice settings');
    if (t.includes('plugins page')) return h('a', { class: 'btn btn-sm', href: '#/plugins' }, icon('plugins', { size: 15 }), 'Open Plugins');
    return null;
  }

  function renderEntry(entry) {
    const { ev } = entry;
    if (ev.role === 'tool-result' || ev.role === 'tool-error') {
      // Fold the result into its tool's row
      const queue = openTools.get(ev.session);
      const row = queue && queue.shift();
      if (row) {
        const err = ev.role === 'tool-error';
        const text = norm(ev.text);
        row._done = true;
        row.classList.add(err ? 'act-err' : 'act-ok');
        row._state.replaceChildren(icon(err ? 'alert' : 'check', { size: 14 }));
        row.append(h('div', { class: 'act-result' }, text.length > 300 ? `${text.slice(0, 297)}…` : text));
        entry.el = null;
        return [];
      }
    }
    const at = parseTime(ev.at) || Date.now();
    const els = [];
    const sessionChanged = ev.session !== lastSession || at - lastAt > 10 * 60 * 1000;
    // Each turn spoken to a puck gets its own label, so it isn't mistaken for typing here
    const puckTurn = ev.role === 'user' && !String(ev.session || '').startsWith('web:');
    if ((sessionChanged || puckTurn) && ev.role !== 'notify') els.push(meta(ev));
    lastSession = ev.session;
    lastAt = at;

    let el;
    switch (ev.role) {
      case 'user':
        el = h('div', { class: 'msg msg-user' }, h('div', { class: 'bubble' }, ev.text));
        closeTool(ev.session);
        break;
      case 'assistant':
        el = h('div', { class: 'msg msg-assistant' },
          h('div', { class: 'avatar', 'aria-hidden': 'true' }, icon('sparkle', { size: 15 })),
          h('div', { class: 'bubble md' }, markdown(ev.text)));
        closeTool(ev.session);
        break;
      case 'tool': {
        const t = parseTool(ev.text);
        const state = h('span', { class: 'act-state' }, spinner('spinner-sm'));
        el = h('div', { class: 'act', title: t.raw || null },
          h('span', { class: 'act-icon' }, icon(toolIcon(t.name), { size: 14 })),
          h('span', { class: 'act-name' }, prettyTool(t.name)),
          t.args ? h('span', { class: 'act-args' }, t.args) : null,
          state);
        el._state = state;
        if (!openTools.has(ev.session)) openTools.set(ev.session, []);
        openTools.get(ev.session).push(el);
        break;
      }
      case 'tool-result':
      case 'tool-error': {
        const err = ev.role === 'tool-error';
        el = h('div', { class: ['act', err ? 'act-err' : 'act-ok'] },
          h('span', { class: 'act-icon' }, icon(err ? 'alert' : 'check', { size: 14 })),
          h('span', { class: 'act-name' }, err ? 'Tool failed' : 'Tool result'),
          h('div', { class: 'act-result' }, norm(ev.text)));
        break;
      }
      case 'error':
        el = h('div', { class: 'msg msg-assistant msg-error' },
          h('div', { class: 'avatar avatar-err', 'aria-hidden': 'true' }, icon('alert', { size: 15 })),
          h('div', { class: 'bubble' }, h('div', null, ev.text), errorExtras(ev.text)));
        closeTool(ev.session);
        break;
      case 'notify':
        el = h('div', { class: 'msg-notify' }, icon('bell', { size: 14 }), h('span', null, ev.text), h('span', { class: 'chat-time' }, fmtClock(ev.at)));
        break;
      default:
        el = h('div', { class: 'msg msg-assistant' }, h('div', { class: 'bubble' }, ev.text));
    }
    if (entry.local) el.classList.add('is-local');
    entry.el = el;
    els.push(el);
    return els;
  }

  function closeTool(session) {
    for (const row of openTools.get(session) || []) {
      row._done = true;
      row._state.replaceChildren();
    }
    openTools.delete(session);
  }

  function visible(entry) {
    return !currentFilter || currentFilter(entry.ev);
  }

  function appendEntry(entry, scroll = true) {
    if (!visible(entry)) return;
    const stick = nearBottom();
    const els = renderEntry(entry);
    for (const e of els) list.append(e);
    rendered.push(entry);
    empty.hidden = true;
    if (scroll && stick) requestAnimationFrame(scrollToEnd);
  }

  function rerender() {
    clear(list);
    rendered = [];
    lastSession = null;
    lastAt = 0;
    openTools.clear();
    for (const entry of all) appendEntry(entry, false);
    empty.hidden = rendered.length > 0;
    requestAnimationFrame(scrollToEnd);
  }

  return {
    el: root,
    /** A line from the hub (or {local:true} for one this page shows before the hub confirms it). */
    add(ev, { local = false } = {}) {
      const text = norm(ev.text);
      if (local) {
        const t = parseTime(ev.at) || Date.now();
        const dup = all.slice(-30).some((x) => !x.local && x.ev.session === ev.session && x.ev.role === ev.role && norm(x.ev.text) === text && Math.abs((parseTime(x.ev.at) || 0) - t) < 20000);
        if (dup) return null;
      } else {
        const mine = all.find((x) => x.local && !x.confirmed && x.ev.session === ev.session && x.ev.role === ev.role && norm(x.ev.text) === text);
        if (mine) {
          mine.confirmed = true;
          mine.el?.classList.remove('is-local');
          return mine;
        }
      }
      const entry = { ev, local, confirmed: false, el: null };
      all.push(entry);
      if (all.length > 800) all = all.slice(-600);
      appendEntry(entry);
      return entry;
    },
    reset(events) {
      const locals = all.filter((x) => x.local && !x.confirmed);
      all = (events || []).map((ev) => ({ ev, local: false, confirmed: false, el: null }));
      for (const l of locals) {
        if (!all.some((x) => x.ev.session === l.ev.session && x.ev.role === l.ev.role && norm(x.ev.text) === norm(l.ev.text))) all.push(l);
      }
      rerender();
    },
    setFilter(fn) {
      currentFilter = fn;
      rerender();
    },
    divider(text) {
      list.append(h('div', { class: 'chat-divider' }, h('span', null, text)));
      lastSession = null;
      requestAnimationFrame(scrollToEnd);
    },
    scrollToEnd,
    nearBottom,
    get count() { return rendered.length; },
  };
}

/** Activity rows returned by POST /api/chat ({icon, text, state}). */
export function activityRows(activities) {
  // Each tool reports "run" then "ok"/"err"; show the final state of each.
  const out = [];
  for (const a of activities || []) {
    if (a.state === 'run') {
      out.push({ ...a });
      continue;
    }
    const open = [...out].reverse().find((x) => x.state === 'run');
    if (open) Object.assign(open, a);
    else out.push({ ...a });
  }
  // The answer is in, so anything still "running" (web search runs on Anthropic's side) is finished.
  return out.map((a) => h('div', { class: ['act', a.state === 'err' ? 'act-err' : a.state === 'ok' ? 'act-ok' : ''] },
    h('span', { class: 'act-icon' }, icon(pluginIconName(a.icon), { size: 14 })),
    h('span', { class: 'act-name' }, a.text),
    h('span', { class: 'act-state' }, a.state === 'run' ? null : icon(a.state === 'err' ? 'alert' : 'check', { size: 14 }))));
}
