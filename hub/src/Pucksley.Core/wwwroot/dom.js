// Small DOM helpers: element builder, dialogs, toasts, formatting, a safe mini-markdown renderer.

import { icon } from './icons.js';

const PROPS = new Set(['value', 'checked', 'disabled', 'hidden', 'selected', 'multiple', 'readOnly', 'required', 'indeterminate', 'open', 'textContent', 'tabIndex']);

/** h('div', {class: 'x', onClick: fn}, child, [children], 'text') */
export function h(tag, props, ...children) {
  const el = document.createElement(tag);
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (v == null || v === false) continue;
      if (k === 'class') el.className = Array.isArray(v) ? v.filter(Boolean).join(' ') : v;
      else if (k === 'style' && typeof v === 'object') {
        for (const [sk, sv] of Object.entries(v)) {
          if (sv == null) continue;
          if (sk.startsWith('--')) el.style.setProperty(sk, String(sv));
          else el.style[sk] = sv;
        }
      }
      else if (k === 'dataset') Object.assign(el.dataset, v);
      else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2).toLowerCase(), v);
      else if (PROPS.has(k)) el[k] = v;
      else if (k === 'for') el.htmlFor = v;
      else el.setAttribute(k, v === true ? '' : String(v));
    }
  }
  append(el, children);
  return el;
}

export function append(el, children) {
  for (const c of children) {
    if (c == null || c === false || c === true) continue;
    if (Array.isArray(c)) append(el, c);
    else if (c instanceof Node) el.append(c);
    else el.append(document.createTextNode(String(c)));
  }
  return el;
}

export function clear(el) {
  while (el.firstChild) el.removeChild(el.firstChild);
  return el;
}

export function replace(el, ...children) {
  clear(el);
  return append(el, children);
}

let seq = 0;
export function uid(prefix = 'id') {
  seq += 1;
  return `${prefix}-${seq}`;
}

export function debounce(fn, ms) {
  let t = null;
  const d = (...args) => {
    clearTimeout(t);
    t = setTimeout(() => fn(...args), ms);
  };
  d.cancel = () => clearTimeout(t);
  return d;
}

export const storage = {
  get(key, fallback = null) {
    try {
      const v = localStorage.getItem(key);
      return v == null ? fallback : JSON.parse(v);
    } catch {
      return fallback;
    }
  },
  set(key, value) {
    try {
      localStorage.setItem(key, JSON.stringify(value));
    } catch { /* private mode etc. */ }
  },
};

export function plural(n, one, many = one + 's') {
  return `${n} ${n === 1 ? one : many}`;
}

export function capitalize(s) {
  return s ? s.charAt(0).toUpperCase() + s.slice(1) : s;
}

// ------------------------------------------------------------------ time

export function parseTime(at) {
  const t = typeof at === 'number' ? at : Date.parse(at);
  return Number.isFinite(t) ? t : null;
}

export function fmtClock(at, seconds = false) {
  const t = parseTime(at);
  if (t == null) return '';
  return new Date(t).toLocaleTimeString([], seconds ? { hour: '2-digit', minute: '2-digit', second: '2-digit' } : { hour: '2-digit', minute: '2-digit' });
}

export function fmtAgo(at) {
  const t = parseTime(at);
  if (t == null || t < 946684800000) return 'never';
  const s = Math.round((Date.now() - t) / 1000);
  if (s < 45) return 'just now';
  const m = Math.round(s / 60);
  if (m < 60) return `${m} min ago`;
  const hrs = Math.round(m / 60);
  if (hrs < 24) return `${hrs} h ago`;
  const d = Math.round(hrs / 24);
  if (d === 1) return 'yesterday';
  if (d < 14) return `${d} days ago`;
  return new Date(t).toLocaleDateString();
}

export function fmtDuration(ms) {
  const s = Math.floor(ms / 1000);
  if (s < 60) return `${s} s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m} min`;
  const hrs = Math.floor(m / 60);
  if (hrs < 48) return `${hrs} h ${m % 60} min`;
  return `${Math.floor(hrs / 24)} days`;
}

// ------------------------------------------------------------------ toasts

function toastHost() {
  let host = document.querySelector('.toasts');
  if (!host) {
    host = h('div', { class: 'toasts', 'aria-live': 'polite', 'aria-atomic': 'false' });
    document.body.append(host);
  }
  return host;
}

/** kind: info | ok | error */
export function toast(message, kind = 'info', timeout = 4500) {
  const ico = kind === 'ok' ? 'check-circle' : kind === 'error' ? 'alert' : 'info';
  const t = h('div', { class: `toast toast-${kind}`, role: kind === 'error' ? 'alert' : 'status' },
    icon(ico, { size: 18 }),
    h('span', { class: 'toast-text' }, message),
    h('button', { class: 'icon-btn toast-close', type: 'button', 'aria-label': 'Dismiss', onClick: () => close() }, icon('x', { size: 16 })));
  const close = () => {
    t.classList.add('toast-out');
    setTimeout(() => t.remove(), 200);
  };
  toastHost().append(t);
  if (timeout) setTimeout(close, timeout);
  return close;
}

// ------------------------------------------------------------------ dialogs

/**
 * A modal dialog. actions: [{label, value, primary, danger, run?: async () => bool|value}]
 * Resolves with the chosen action's value (or `run`'s result), or dismissValue on Esc/backdrop.
 */
export function openDialog({ title, body, actions = [{ label: 'OK', value: true, primary: true }], dismissValue = null, onOpen, wide = false }) {
  return new Promise((resolve) => {
    const titleId = uid('dlg');
    const errorEl = h('p', { class: 'dialog-error', role: 'alert', hidden: true });
    const buttons = actions.map((a) => h('button', {
      type: a.primary ? 'submit' : 'button',
      class: ['btn', a.primary && 'btn-primary', a.danger && (a.primary ? 'btn-danger-solid' : 'btn-danger')],
      onClick: async (e) => {
        e.preventDefault();
        if (!a.run) return finish(a.value);
        errorEl.hidden = true;
        buttons.forEach((b) => { b.disabled = true; });
        try {
          const r = await a.run();
          if (r !== false) finish(r === undefined ? a.value : r);
        } catch (err) {
          errorEl.textContent = err?.message || String(err);
          errorEl.hidden = false;
        } finally {
          buttons.forEach((b) => { b.disabled = false; });
        }
      },
    }, a.label));
    const form = h('form', { class: 'dialog-inner', method: 'dialog', onSubmit: (e) => { e.preventDefault(); buttons[actions.findIndex((a) => a.primary)]?.click(); } },
      h('div', { class: 'dialog-head' },
        h('h2', { id: titleId, class: 'dialog-title' }, title),
        h('button', { type: 'button', class: 'icon-btn', 'aria-label': 'Close', onClick: () => finish(dismissValue) }, icon('x'))),
      h('div', { class: 'dialog-body' }, body),
      errorEl,
      h('div', { class: 'dialog-actions' }, buttons));
    const dlg = h('dialog', { class: ['dialog', wide && 'dialog-wide'], 'aria-labelledby': titleId }, form);
    let done = false;
    function finish(value) {
      if (done) return;
      done = true;
      dlg.close();
      dlg.remove();
      resolve(value);
    }
    dlg.addEventListener('cancel', (e) => { e.preventDefault(); finish(dismissValue); });
    dlg.addEventListener('mousedown', (e) => {
      if (e.target === dlg) {
        const r = dlg.getBoundingClientRect();
        const inside = e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom;
        if (!inside) finish(dismissValue);
      }
    });
    document.body.append(dlg);
    dlg.showModal();
    onOpen?.(dlg);
  });
}

export function confirmDialog({ title, message, confirmLabel = 'OK', cancelLabel = 'Cancel', danger = false }) {
  return openDialog({
    title,
    body: typeof message === 'string' ? h('p', null, message) : message,
    dismissValue: false,
    actions: [
      { label: cancelLabel, value: false },
      { label: confirmLabel, value: true, primary: true, danger },
    ],
  });
}

// ------------------------------------------------------------------ text

/** Text with bare URLs and well-known domains turned into links. */
export function linkify(text) {
  const frag = document.createDocumentFragment();
  const re = /(https?:\/\/[^\s<>()]+[^\s<>().,;:!?'"])|\b((?:console\.anthropic\.com|developer\.spotify\.com|platform\.openai\.com)(?:\/[^\s]*)?)/g;
  let last = 0;
  let m;
  while ((m = re.exec(text))) {
    if (m.index > last) frag.append(text.slice(last, m.index));
    const url = m[1] || `https://${m[2]}`;
    frag.append(h('a', { href: url, target: '_blank', rel: 'noopener noreferrer' }, m[0]));
    last = m.index + m[0].length;
  }
  if (last < text.length) frag.append(text.slice(last));
  return frag;
}

const INLINE = /(\*\*([^*\n]+?)\*\*)|(`([^`\n]+?)`)|(\[([^\]\n]+)\]\((https?:\/\/[^)\s]+)\))|(https?:\/\/[^\s<>()]+[^\s<>().,;:!?'"])|(\*([^*\s][^*\n]*?)\*)/g;

function inline(text) {
  const frag = document.createDocumentFragment();
  let last = 0;
  let m;
  INLINE.lastIndex = 0;
  while ((m = INLINE.exec(text))) {
    if (m.index > last) frag.append(text.slice(last, m.index));
    if (m[1]) frag.append(h('strong', null, m[2]));
    else if (m[3]) frag.append(h('code', null, m[4]));
    else if (m[5]) frag.append(h('a', { href: m[7], target: '_blank', rel: 'noopener noreferrer' }, m[6]));
    else if (m[8]) frag.append(h('a', { href: m[8], target: '_blank', rel: 'noopener noreferrer' }, m[8]));
    else if (m[9]) frag.append(h('em', null, m[10]));
    last = m.index + m[0].length;
  }
  if (last < text.length) frag.append(text.slice(last));
  return frag;
}

/** A safe subset of Markdown (paragraphs, lists, code, bold, italics, links) as DOM nodes. */
export function markdown(text) {
  const frag = document.createDocumentFragment();
  const lines = String(text ?? '').replace(/\r\n/g, '\n').split('\n');
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (/^\s*```/.test(line)) {
      const code = [];
      i++;
      while (i < lines.length && !/^\s*```/.test(lines[i])) code.push(lines[i++]);
      i++;
      frag.append(h('pre', null, h('code', null, code.join('\n'))));
      continue;
    }
    if (/^\s*$/.test(line)) { i++; continue; }
    if (/^\s*[-*•]\s+/.test(line)) {
      const ul = h('ul');
      while (i < lines.length && /^\s*[-*•]\s+/.test(lines[i])) ul.append(h('li', null, inline(lines[i++].replace(/^\s*[-*•]\s+/, ''))));
      frag.append(ul);
      continue;
    }
    if (/^\s*\d+[.)]\s+/.test(line)) {
      const ol = h('ol');
      while (i < lines.length && /^\s*\d+[.)]\s+/.test(lines[i])) ol.append(h('li', null, inline(lines[i++].replace(/^\s*\d+[.)]\s+/, ''))));
      frag.append(ol);
      continue;
    }
    if (/^\s*#{1,6}\s+/.test(line)) {
      frag.append(h('p', { class: 'md-h' }, h('strong', null, inline(line.replace(/^\s*#{1,6}\s+/, '')))));
      i++;
      continue;
    }
    const para = [];
    while (i < lines.length && !/^\s*$/.test(lines[i]) && !/^\s*([-*•]\s+|\d+[.)]\s+|#{1,6}\s+|```)/.test(lines[i])) para.push(lines[i++]);
    if (!para.length) para.push(lines[i++]);
    const p = h('p');
    para.forEach((l, idx) => {
      if (idx) p.append(h('br'));
      p.append(inline(l));
    });
    frag.append(p);
  }
  return frag;
}

export async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    const ta = h('textarea', { style: { position: 'fixed', opacity: '0' } });
    ta.value = text;
    document.body.append(ta);
    ta.select();
    let ok = false;
    try { ok = document.execCommand('copy'); } catch { ok = false; }
    ta.remove();
    return ok;
  }
}

/** A button that copies `text`, showing "Copied". */
export function copyButton(text, label = 'Copy') {
  const b = h('button', { type: 'button', class: 'btn btn-sm' }, icon('copy', { size: 16 }), h('span', null, label));
  b.addEventListener('click', async () => {
    const ok = await copyText(typeof text === 'function' ? text() : text);
    b.lastChild.textContent = ok ? 'Copied' : 'Copy failed';
    setTimeout(() => { b.lastChild.textContent = label; }, 1600);
  });
  return b;
}

export function spinner(cls = '') {
  return h('span', { class: `spinner ${cls}`, 'aria-hidden': 'true' });
}

/** A callout box. kind: info | ok | warn | error */
export function callout(kind, ...children) {
  const ico = { info: 'info', ok: 'check-circle', warn: 'alert', error: 'alert' }[kind] || 'info';
  return h('div', { class: `callout callout-${kind}` }, icon(ico, { size: 18 }), h('div', { class: 'callout-body' }, children));
}

export function emptyState({ iconName = 'info', title, text, actions }) {
  return h('div', { class: 'empty' },
    h('div', { class: 'empty-icon' }, icon(iconName, { size: 26 })),
    h('h3', null, title),
    text ? h('p', null, text) : null,
    actions ? h('div', { class: 'empty-actions' }, actions) : null);
}
