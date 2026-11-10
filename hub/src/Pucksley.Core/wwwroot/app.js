// Pucksley hub page: the shell (header, navigation, theme), the router, and the admin-key screen.

import { h, clear, storage, confirmDialog } from './dom.js';
import { icon } from './icons.js';
import { onUnauthorized } from './api.js';
import { store, on, refreshStatus, startLive, loadHistory, loadSection } from './store.js';
import { anyDirty, discardAll } from './forms.js';
import { needsAttention } from './components.js';
import home from './home.js';
import talk from './talk.js';
import pucks from './pucks.js';
import plugins from './plugins.js';
import { assistantPage, voicePage, householdPage } from './settings.js';
import activity from './activity.js';

const PAGES = {
  '': home,
  talk,
  pucks,
  plugins,
  assistant: assistantPage,
  voice: voicePage,
  household: householdPage,
  activity,
};

const NAV = [
  { page: '', label: 'Home', icon: 'home' },
  { page: 'talk', label: 'Talk', icon: 'talk' },
  { page: 'pucks', label: 'Pucks', icon: 'puck' },
  { page: 'plugins', label: 'Plugins', icon: 'plugins' },
  { section: 'Settings' },
  { page: 'assistant', label: 'Assistant', icon: 'sparkle' },
  { page: 'voice', label: 'Voice', icon: 'voice' },
  { page: 'household', label: 'Household', icon: 'household' },
  { section: 'Watch' },
  { page: 'activity', label: 'Activity', icon: 'activity' },
];

const app = document.getElementById('app');
let main;
let current = null; // {page, instance, route}
let navLinks = {};
let chipsEl;
let hubNameEl;
let connBanner;

// ------------------------------------------------------------------ theme

const THEMES = ['system', 'light', 'dark'];

function applyTheme(t) {
  if (t === 'light' || t === 'dark') document.documentElement.dataset.theme = t;
  else delete document.documentElement.dataset.theme;
}

function themeButton() {
  let t = storage.get('puck.theme', 'system');
  if (!THEMES.includes(t)) t = 'system';
  const names = { system: 'Match the system', light: 'Light', dark: 'Dark' };
  const icons = { system: 'auto', light: 'sun', dark: 'moon' };
  const btn = h('button', { type: 'button', class: 'icon-btn theme-btn' });
  const render = () => {
    btn.replaceChildren(icon(icons[t], { size: 19 }));
    btn.title = `Theme: ${names[t]} (click to change)`;
    btn.setAttribute('aria-label', `Theme: ${names[t]}. Change theme`);
  };
  btn.addEventListener('click', () => {
    t = THEMES[(THEMES.indexOf(t) + 1) % THEMES.length];
    storage.set('puck.theme', t);
    applyTheme(t);
    render();
  });
  render();
  return btn;
}

// ------------------------------------------------------------------ shell

function brandMark(size = 30) {
  return h('span', { class: 'brand-mark', style: { '--size': `${size}px` }, 'aria-hidden': 'true' }, h('span', { class: 'brand-mark-inner' }));
}

function buildShell() {
  clear(app);
  app.removeAttribute('aria-busy');
  hubNameEl = h('span', { class: 'brand-hub' }, store.status?.hub?.name || 'Pucksley hub');
  chipsEl = h('div', { class: 'chips', role: 'list', 'aria-label': 'Hub status' });
  const menuBtn = h('button', { type: 'button', class: 'icon-btn menu-btn', 'aria-label': 'Menu', 'aria-controls': 'sidebar', 'aria-expanded': 'false', onClick: () => toggleNav() }, icon('menu', { size: 20 }));
  const topbar = h('header', { class: 'topbar' },
    menuBtn,
    h('a', { class: 'brand', href: '#/', 'aria-label': 'Pucksley home' }, brandMark(), h('span', { class: 'brand-text' }, h('span', { class: 'brand-name' }, 'Pucksley'), hubNameEl)),
    chipsEl,
    themeButton());

  const nav = h('nav', { class: 'nav', 'aria-label': 'Main' });
  navLinks = {};
  for (const item of NAV) {
    if (item.section) {
      nav.append(h('div', { class: 'nav-section' }, item.section));
      continue;
    }
    const badge = h('span', { class: 'nav-badge', hidden: true });
    const a = h('a', { href: `#/${item.page}`, class: 'nav-link', onClick: () => closeNav() }, icon(item.icon, { size: 19 }), h('span', null, item.label), badge);
    a._badge = badge;
    navLinks[item.page] = a;
    nav.append(a);
  }
  const versionEl = h('div', { class: 'sidebar-foot' });
  const sidebar = h('aside', { class: 'sidebar', id: 'sidebar' }, nav, versionEl);
  sidebar._version = versionEl;
  const scrim = h('div', { class: 'scrim', onClick: () => closeNav() });
  connBanner = h('div', { class: 'conn-banner', role: 'status', hidden: true }, icon('alert', { size: 16 }), h('span', null, 'Can’t reach the hub right now. Retrying…'));
  main = h('main', { id: 'main', class: 'main', tabindex: '-1' });
  const skip = h('a', { class: 'skip-link', href: '#main', onClick: (e) => { e.preventDefault(); main.focus(); } }, 'Skip to content');

  app.append(skip, topbar, sidebar, scrim, h('div', { class: 'main-col' }, connBanner, main));
  app._menuBtn = menuBtn;
  app._sidebar = sidebar;

  // Keep the sticky offsets right when the header wraps (phones)
  const setTop = () => document.documentElement.style.setProperty('--topbar-h', `${topbar.offsetHeight}px`);
  setTop();
  if (window.ResizeObserver) new ResizeObserver(setTop).observe(topbar);
  else window.addEventListener('resize', setTop);

  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && app.classList.contains('nav-open')) closeNav();
  });
}

function toggleNav() {
  const open = !app.classList.contains('nav-open');
  app.classList.toggle('nav-open', open);
  app._menuBtn?.setAttribute('aria-expanded', String(open));
  if (open) app._sidebar?.querySelector('.nav-link')?.focus();
}

function closeNav() {
  if (!app.classList.contains('nav-open')) return;
  app.classList.remove('nav-open');
  app._menuBtn?.setAttribute('aria-expanded', 'false');
}

function chip({ href, level, ico, label, title, pulse = false }) {
  return h('a', { class: ['chip', `chip-${level}`, pulse && 'chip-pulse'], href, title, role: 'listitem' },
    h('span', { class: `dot ${level}`, 'aria-hidden': 'true' }),
    icon(ico, { size: 15 }),
    h('span', { class: 'chip-label' }, label));
}

function renderChrome(s) {
  if (!chipsEl) return;
  hubNameEl.textContent = s.hub?.name || 'Pucksley hub';
  document.title = `${current ? PAGES[current.page].title + ' · ' : ''}${s.hub?.name || 'Pucksley'}`;
  const chips = [];

  const a = s.assistant || {};
  chips.push(chip({
    href: '#/assistant', ico: 'sparkle',
    level: a.ready ? 'ok' : 'warn',
    label: a.ready ? 'Claude' : 'Claude: add key',
    title: a.ready ? `Claude is ready (${a.model}, ${a.toolCount} tools)` : 'Claude needs an API key: open the Assistant page',
  }));

  const stt = s.voice?.stt;
  chips.push(chip({
    href: '#/voice', ico: 'ear',
    level: stt?.ready ? 'ok' : 'warn',
    label: stt ? stt.name : 'Hearing: not set up',
    title: stt ? `Speech recognition: ${stt.name}${stt.ready ? '' : ' (needs setting up)'}` : 'No speech recognition engine yet',
  }));

  const tts = s.voice?.tts;
  const textOnly = store.sections.voice?.values?.ttsEngine?.toLowerCase?.() === 'none';
  chips.push(chip({
    href: '#/voice', ico: 'speaker',
    level: tts?.ready ? 'ok' : textOnly ? 'idle' : 'warn',
    label: tts ? tts.name : textOnly ? 'Text only' : 'Voice: not set up',
    title: tts ? `Voice: ${tts.name}` : textOnly ? 'Answers are shown as text (no voice)' : 'No voice yet: the puck will show text only',
  }));

  const connected = (s.pucks || []).filter((p) => p.connected).length;
  const pending = (s.pending || []).length;
  chips.push(chip({
    href: '#/pucks', ico: 'puck',
    level: pending ? 'accent' : connected ? 'ok' : 'idle',
    label: pending ? `${pending} waiting` : connected ? `${connected} puck${connected === 1 ? '' : 's'}` : 'No pucks',
    title: pending ? 'A puck is waiting for approval' : `${connected} connected of ${(s.pucks || []).length} known`,
    pulse: pending > 0,
  }));
  const chipSig = chips.map((c) => c.outerHTML).join('');
  if (chipSig !== chipsEl._sig) {
    chipsEl._sig = chipSig;
    chipsEl.replaceChildren(...chips);
  }

  // Navigation badges
  const pb = navLinks.pucks?._badge;
  if (pb) {
    pb.hidden = !pending;
    pb.textContent = pending ? String(pending) : '';
    pb.className = 'nav-badge nav-badge-accent';
  }
  const attention = (s.plugins || []).filter(needsAttention).length;
  const plb = navLinks.plugins?._badge;
  if (plb) {
    plb.hidden = !attention;
    plb.textContent = attention ? String(attention) : '';
    plb.className = 'nav-badge nav-badge-warn';
    plb.title = attention ? `${attention} need${attention === 1 ? 's' : ''} attention` : '';
  }
  const ab = navLinks.assistant?._badge;
  if (ab) {
    ab.hidden = !!a.ready;
    ab.textContent = '!';
    ab.className = 'nav-badge nav-badge-warn';
  }
  if (app._sidebar?._version) app._sidebar._version.textContent = s.hub?.version ? `Pucksley hub ${s.hub.version}` : '';
}

// ------------------------------------------------------------------ routing

function parse(hash) {
  const raw = String(hash || '').replace(/^#\/?/, '');
  const [path, query] = raw.split('?');
  const parts = path.split('/').filter(Boolean).map((p) => {
    try { return decodeURIComponent(p); } catch { return p; }
  });
  return { page: parts[0] || '', rest: parts.slice(1), query: new URLSearchParams(query || ''), hash: `#/${raw}` };
}

function isRoute(hash) {
  return hash === '' || hash === '#' || hash.startsWith('#/');
}

function render() {
  const r = parse(location.hash);
  if (!(r.page in PAGES)) {
    location.replace('#/');
    return;
  }
  if (current && current.page === r.page && current.instance.route) {
    current.route = r;
    current.instance.route(r);
    return;
  }
  if (current) {
    try { current.instance.unmount?.(); } catch (e) { console.error(e); }
  }
  clear(main);
  main.className = `main page-${r.page || 'home'}`;
  const page = PAGES[r.page];
  let instance = {};
  try {
    instance = page.mount(main, r) || {};
  } catch (e) {
    console.error(e);
    main.append(h('div', { class: 'callout callout-error' }, `This page failed to load: ${e.message}`));
  }
  current = { page: r.page, instance, route: r };
  for (const [p, a] of Object.entries(navLinks)) {
    if (p === r.page) a.setAttribute('aria-current', 'page');
    else a.removeAttribute('aria-current');
  }
  document.title = `${page.title} · ${store.status?.hub?.name || 'Pucksley'}`;
  window.scrollTo(0, 0);
  if (store.status) {
    try { instance.update?.(store.status); } catch (e) { console.error(e); }
  }
}

let lastHash = location.hash;
let bypass = false;

function leavingPage(next, prev) {
  if (next.page !== prev.page) return true;
  // The plugin panel has its own form
  return next.page === 'plugins' && (next.rest[0] || '') !== (prev.rest[0] || '');
}

async function onHashChange() {
  if (!isRoute(location.hash)) return; // e.g. the skip link
  if (bypass) {
    bypass = false;
    lastHash = location.hash;
    render();
    return;
  }
  const next = parse(location.hash);
  const prev = parse(lastHash);
  if (leavingPage(next, prev) && anyDirty()) {
    const target = location.hash;
    history.replaceState(null, '', lastHash || '#/');
    const ok = await confirmDialog({
      title: 'Leave without saving?',
      message: 'Some changes on this page haven’t been saved yet.',
      confirmLabel: 'Leave without saving',
      cancelLabel: 'Stay here',
      danger: true,
    });
    if (!ok) return;
    discardAll();
    bypass = true;
    location.hash = target;
    return;
  }
  lastHash = location.hash;
  render();
}

window.addEventListener('beforeunload', (e) => {
  if (anyDirty()) {
    e.preventDefault();
    e.returnValue = '';
  }
});

// ------------------------------------------------------------------ the admin key

function showKeyScreen() {
  if (store.authNeeded) return;
  store.authNeeded = true;
  const params = new URLSearchParams(location.search);
  const tried = params.has('key');
  const input = h('input', { class: 'input', id: 'admin-key', type: 'password', autocomplete: 'current-password', spellcheck: 'false', placeholder: 'e.g. 3f9c2a…', required: true });
  const form = h('form', {
    class: 'auth-form',
    onSubmit: (e) => {
      e.preventDefault();
      const key = input.value.trim();
      if (!key) return input.focus();
      location.href = `${location.pathname}?key=${encodeURIComponent(key)}${location.hash}`;
    },
  },
  h('label', { class: 'field-label', for: 'admin-key' }, 'Admin key'),
  h('div', { class: 'auth-row' }, input, h('button', { type: 'submit', class: 'btn btn-primary' }, 'Continue')));
  clear(app);
  app.removeAttribute('aria-busy');
  app.append(h('div', { class: 'auth' },
    h('div', { class: 'auth-card card' },
      brandMark(44),
      h('h1', null, 'This hub needs its admin key'),
      h('p', { class: 'lede' }, 'You’re opening the hub from another device, so it asks for its key first. You only need to do this once on each device.'),
      tried ? h('div', { class: 'callout callout-error' }, icon('alert', { size: 18 }), h('div', null, 'That key didn’t work. Check it and try again.')) : null,
      form,
      h('div', { class: 'auth-help' },
        h('h2', null, 'Where’s the key?'),
        h('ul', null,
          h('li', null, 'The hub prints a ready-made link when it starts: look in its log for ', h('em', null, '“Open the hub from another computer with …?key=…”'), '.'),
          h('li', null, 'On the computer running the hub, open ', h('code', null, `http://localhost:${location.port || '80'}/`), ': it doesn’t need the key there, and its Home page shows a link for your phone.'))))));
  setTimeout(() => input.focus(), 50);
}

onUnauthorized(showKeyScreen);

// ------------------------------------------------------------------ start

async function start() {
  applyTheme(storage.get('puck.theme', 'system'));
  await refreshStatus();
  if (store.authNeeded) return;
  if (!store.status) {
    // The hub isn't answering: keep trying, show a friendly wait screen meanwhile.
    clear(app);
    app.append(h('div', { class: 'boot' }, h('div', { class: 'boot-orb' }), h('p', null, 'Can’t reach the hub yet. Retrying…')));
    const off = on('status', () => { off(); boot(); });
    startLive();
    return;
  }
  boot();
  startLive();
}

function boot() {
  // A ?key= in the address bar has done its job (the hub set a cookie): tidy it away.
  if (new URLSearchParams(location.search).has('key')) history.replaceState(null, '', location.pathname + location.hash);
  buildShell();
  on('status', (s) => {
    renderChrome(s);
    try { current?.instance.update?.(s); } catch (e) { console.error(e); }
  });
  on('section', ({ name }) => { if (name === 'voice' && store.status) renderChrome(store.status); });
  on('reachable', (ok) => { connBanner.hidden = ok; });
  on('live', (ok) => { app.classList.toggle('is-live', ok); });
  window.addEventListener('hashchange', onHashChange);
  if (!isRoute(location.hash)) history.replaceState(null, '', `${location.pathname}#/`);
  lastHash = location.hash;
  renderChrome(store.status);
  render();
  loadHistory();
  loadSection('voice');
}

start();
