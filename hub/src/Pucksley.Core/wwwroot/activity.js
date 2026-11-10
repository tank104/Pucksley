// Activity: the hub's live log (filterable) and the conversation history.

import { h, clear, fmtClock } from './dom.js';
import { icon } from './icons.js';
import { store, on } from './store.js';
import { createChatView } from './components.js';
import { selectWrap } from './forms.js';

const RANK = { Trace: 0, Debug: 1, Information: 2, Warning: 3, Error: 4, Critical: 5 };
const SHORT = { Trace: 'trace', Debug: 'debug', Information: 'info', Warning: 'warn', Error: 'error', Critical: 'crit' };

function lvlClass(level) {
  const r = RANK[level] ?? 2;
  return r >= 4 ? 'err' : r === 3 ? 'warn' : r <= 1 ? 'debug' : 'info';
}

function logLine(ev) {
  return h('div', { class: `log-line lvl-${lvlClass(ev.level)}` },
    h('time', { class: 'log-time', datetime: ev.at }, fmtClock(ev.at, true)),
    h('span', { class: 'log-lvl' }, SHORT[ev.level] || String(ev.level || '').toLowerCase()),
    h('span', { class: 'log-cat', title: ev.category }, ev.category || ''),
    h('span', { class: 'log-msg' }, ev.message || ''));
}

export default {
  title: 'Activity',
  mount(root, route) {
    const offs = [];
    let tab = route?.rest?.[0] === 'conversations' ? 'conversations' : 'log';

    // ---------------------------------------------------------------- tabs
    const tabLog = h('a', { href: '#/activity', class: 'tab', role: 'tab' }, icon('activity', { size: 16 }), 'Log');
    const tabConv = h('a', { href: '#/activity/conversations', class: 'tab', role: 'tab' }, icon('talk', { size: 16 }), 'Conversations');
    const tabs = h('div', { class: 'tabs', role: 'tablist', 'aria-label': 'Activity' }, tabLog, tabConv);

    // ---------------------------------------------------------------- log
    let level = 'all';
    let category = '';
    let query = '';
    let paused = false;
    let missed = 0;
    let cutoff = 0; // lines before this time are cleared from view

    const levelSel = h('select', { class: 'input select select-sm', 'aria-label': 'Which messages' },
      h('option', { value: 'all' }, 'Everything'),
      h('option', { value: 'warn' }, 'Warnings and errors'),
      h('option', { value: 'err' }, 'Errors only'));
    const catSel = h('select', { class: 'input select select-sm', 'aria-label': 'From' });
    const search = h('input', { class: 'input input-search input-sm', type: 'search', placeholder: 'Filter', 'aria-label': 'Filter the log', autocomplete: 'off' });
    const pauseBtn = h('button', { type: 'button', class: 'btn btn-sm' });
    const clearBtn = h('button', { type: 'button', class: 'btn btn-sm btn-ghost', title: 'Clear the view (the hub keeps its log)' }, 'Clear');
    const saveBtn = h('button', { type: 'button', class: 'btn btn-sm btn-ghost', title: 'Download what’s shown as a text file' }, 'Download');
    const box = h('div', { class: 'log-box', role: 'log', 'aria-live': 'off', tabindex: '0' });
    const empty = h('div', { class: 'log-empty' }, 'Nothing here yet.');
    const logPanel = h('div', { class: 'tab-panel', role: 'tabpanel' },
      h('div', { class: 'toolbar toolbar-tight' }, selectWrap(levelSel), selectWrap(catSel), search, h('div', { class: 'toolbar-end' }, pauseBtn, clearBtn, saveBtn)),
      h('div', { class: 'log-wrap' }, box, empty));

    const passes = (ev) => {
      if ((Date.parse(ev.at) || 0) < cutoff) return false;
      const r = RANK[ev.level] ?? 2;
      if (level === 'warn' && r < 3) return false;
      if (level === 'err' && r < 4) return false;
      if (category && ev.category !== category) return false;
      if (query && !`${ev.category} ${ev.message}`.toLowerCase().includes(query)) return false;
      return true;
    };
    const nearBottom = () => box.scrollHeight - box.scrollTop - box.clientHeight < 60;

    function renderPause() {
      pauseBtn.replaceChildren(icon(paused ? 'play' : 'pause', { size: 15 }), paused ? (missed ? `Resume (${missed} new)` : 'Resume') : 'Pause');
      pauseBtn.setAttribute('aria-pressed', String(paused));
    }

    let cats = '';
    function renderCats() {
      const set = [...new Set(store.logs.map((e) => e.category).filter(Boolean))].sort((a, b) => a.localeCompare(b));
      const sig = set.join('|');
      if (sig === cats) return;
      cats = sig;
      catSel.replaceChildren(h('option', { value: '' }, 'All sources'), ...set.map((c) => h('option', { value: c }, c)));
      catSel.value = set.includes(category) ? category : '';
    }

    function renderLog() {
      clear(box);
      const frag = document.createDocumentFragment();
      let n = 0;
      for (const ev of store.logs) {
        if (!passes(ev)) continue;
        frag.append(logLine(ev));
        n++;
      }
      box.append(frag);
      empty.hidden = n > 0;
      missed = 0;
      renderPause();
      box.scrollTop = box.scrollHeight;
    }

    levelSel.addEventListener('change', () => { level = levelSel.value; renderLog(); });
    catSel.addEventListener('change', () => { category = catSel.value; renderLog(); });
    let st = null;
    search.addEventListener('input', () => {
      clearTimeout(st);
      st = setTimeout(() => { query = search.value.trim().toLowerCase(); renderLog(); }, 150);
    });
    pauseBtn.addEventListener('click', () => {
      paused = !paused;
      if (!paused) renderLog();
      else renderPause();
    });
    clearBtn.addEventListener('click', () => {
      cutoff = Date.now();
      renderLog();
    });
    saveBtn.addEventListener('click', () => {
      const lines = store.logs.filter(passes).map((e) => `${e.at}  ${String(e.level).padEnd(11)} ${String(e.category || '').padEnd(14)} ${e.message}`);
      const blob = new Blob([lines.join('\n') + '\n'], { type: 'text/plain' });
      const a = h('a', { href: URL.createObjectURL(blob), download: `puck-hub-log-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')}.txt` });
      document.body.append(a);
      a.click();
      setTimeout(() => { URL.revokeObjectURL(a.href); a.remove(); }, 1000);
    });

    offs.push(on('log', (ev) => {
      renderCats();
      if (!passes(ev)) return;
      if (paused) {
        missed++;
        renderPause();
        return;
      }
      const stick = nearBottom();
      box.append(logLine(ev));
      empty.hidden = true;
      while (box.childElementCount > 1500) box.firstElementChild.remove();
      if (stick) box.scrollTop = box.scrollHeight;
    }));
    offs.push(on('logs-reset', () => { renderCats(); if (!paused) renderLog(); }));

    // ---------------------------------------------------------------- conversations
    const convBox = h('div', { class: 'conv-box' });
    const chat = createChatView({ compact: true, scroller: convBox, emptyText: 'No conversations yet. Talk to a puck or use the Talk page.' });
    convBox.append(chat.el);
    let convFilter = 'all';
    const convSel = h('select', { class: 'input select select-sm', 'aria-label': 'Which conversations' },
      h('option', { value: 'all' }, 'All conversations'),
      h('option', { value: 'puck' }, 'Pucks only'),
      h('option', { value: 'web' }, 'This page and the browser only'));
    convSel.addEventListener('change', () => {
      convFilter = convSel.value;
      chat.setFilter(convFilter === 'all' ? null
        : convFilter === 'puck' ? (ev) => String(ev.session || '').startsWith('puck:') || ev.role === 'notify'
          : (ev) => String(ev.session || '').startsWith('web:'));
    });
    const convPanel = h('div', { class: 'tab-panel', role: 'tabpanel' },
      h('div', { class: 'toolbar toolbar-tight' }, selectWrap(convSel), h('p', { class: 'muted small toolbar-note' }, 'The hub keeps the last 200 lines.')),
      convBox);
    chat.reset(store.conversation);
    offs.push(on('conversation', (ev) => chat.add(ev)));
    offs.push(on('conversation-reset', (list) => chat.reset(list)));

    root.append(
      h('div', { class: 'page-head' },
        h('div', null,
          h('h1', null, 'Activity'),
          h('p', { class: 'lede' }, 'What the hub is doing, as it happens. Handy when something doesn’t work.'))),
      tabs, logPanel, convPanel);

    function showTab() {
      const isLog = tab === 'log';
      logPanel.hidden = !isLog;
      convPanel.hidden = isLog;
      tabLog.setAttribute('aria-selected', String(isLog));
      tabConv.setAttribute('aria-selected', String(!isLog));
      if (isLog) requestAnimationFrame(() => { box.scrollTop = box.scrollHeight; });
      else requestAnimationFrame(() => chat.scrollToEnd());
    }

    renderCats();
    renderLog();
    showTab();

    return {
      route(r) {
        tab = r.rest[0] === 'conversations' ? 'conversations' : 'log';
        showTab();
      },
      unmount() {
        offs.forEach((f) => f());
        clearTimeout(st);
      },
    };
  },
};
