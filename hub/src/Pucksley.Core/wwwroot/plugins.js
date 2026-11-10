// Plugins: a grid of cards (status, on/off, what they offer) and a detail panel with settings,
// action buttons and the tools Claude gets.

import { h, clear, toast, plural, callout, spinner, emptyState, uid } from './dom.js';
import { icon } from './icons.js';
import { get, put, post, enc } from './api.js';
import { store, on, refreshStatus, pluginById } from './store.js';
import { createForm, switchEl } from './forms.js';
import { statusDot, kindBadge, pluginTile, needsAttention, levelClass } from './components.js';

const KIND_NAMES = { 'speech-to-text': 'Speech to text', voice: 'Voices', media: 'Media' };

/** Turn a plugin on or off. Resolves with the plugin's detail, or null on failure. */
async function setEnabled(p, enabled) {
  try {
    const d = await post(`/api/plugins/${enc(p.id)}/enabled`, { enabled });
    const level = levelClass(d?.status?.level);
    if (enabled && level === 'err') toast(`${p.name} is on, but it reported a problem: ${d.status.message}`, 'error', 7000);
    else toast(`${p.name} is ${enabled ? 'on' : 'off'}.`, 'ok', 2200);
    refreshStatus();
    return d;
  } catch (e) {
    toast(e.message, 'error');
    return null;
  }
}

function cardSig(p) {
  return JSON.stringify([p.name, p.description, p.icon, p.enabled, p.status?.level, p.status?.message, p.toolCount, p.kinds]);
}

function pluginCard(p, busy) {
  const sw = switchEl({ checked: p.enabled, label: `${p.name} on or off` });
  sw.input.addEventListener('change', async () => {
    const want = sw.input.checked;
    sw.input.disabled = true;
    busy.add(p.id);
    status.replaceChildren(spinner('spinner-sm'), h('span', null, want ? 'Starting…' : 'Stopping…'));
    const d = await setEnabled(p, want);
    busy.delete(p.id);
    sw.input.disabled = false;
    if (!d) sw.input.checked = !want;
  });
  const status = h('div', { class: 'pcard-status' }, statusDot(p.enabled ? p.status?.level : 'Idle'), h('span', null, p.enabled ? p.status?.message || 'On' : 'Off'));
  return h('article', { class: ['pcard', !p.enabled && 'is-off', needsAttention(p) && 'is-attn'], dataset: { id: p.id } },
    h('div', { class: 'pcard-top' }, pluginTile(p.icon, 44), h('div', { class: 'pcard-switch' }, sw)),
    h('h3', { class: 'pcard-name' }, h('a', { href: `#/plugins/${enc(p.id)}`, class: 'stretched' }, p.name)),
    h('p', { class: 'pcard-desc' }, p.description || ''),
    status,
    h('div', { class: 'pcard-foot' },
      (p.kinds || []).map(kindBadge),
      p.toolCount ? h('span', { class: 'pcard-tools' }, icon('tool', { size: 13 }), plural(p.toolCount, 'tool')) : null));
}

// ------------------------------------------------------------------ the detail panel

function actionBlock(pluginId, a, onDone) {
  const fields = (a.inputs || []).map((i) => {
    const id = uid('act');
    const input = h('input', { class: 'input', id, type: i.secret ? 'password' : 'text', placeholder: i.placeholder || null, autocomplete: i.secret ? 'new-password' : 'off', spellcheck: 'false' });
    return { key: i.key, input, el: h('div', { class: 'field' }, h('label', { class: 'field-label', for: id }, i.label || i.key), input) };
  });
  const btn = h('button', { type: 'submit', class: 'btn' }, a.label);
  const result = h('div', { class: 'action-result', 'aria-live': 'polite' });
  const form = h('form', {
    class: 'action',
    onSubmit: async (e) => {
      e.preventDefault();
      const inputs = {};
      for (const f of fields) inputs[f.key] = f.input.value;
      btn.disabled = true;
      const label = btn.textContent;
      btn.replaceChildren(spinner('spinner-sm'), 'Working…');
      result.replaceChildren();
      try {
        const r = await post(`/api/plugins/${enc(pluginId)}/actions/${enc(a.id)}`, { inputs });
        const parts = [h('div', null, r?.message || (r?.ok === false ? 'That didn’t work.' : 'Done.'))];
        if (r?.openUrl) {
          let opened = null;
          try {
            opened = window.open(r.openUrl, '_blank');
            if (opened) opened.opener = null;
          } catch { opened = null; }
          parts.push(h('div', { class: 'action-open' },
            h('a', { class: 'btn btn-sm btn-primary', href: r.openUrl, target: '_blank', rel: 'noopener noreferrer' }, icon('external', { size: 15 }), opened ? 'Open the page again' : 'Open the page'),
            opened ? h('span', { class: 'muted small' }, 'It opened in a new tab. Come back here when you’re done.') : h('span', { class: 'muted small' }, 'Your browser blocked the new tab; use this button.')));
        }
        result.replaceChildren(callout(r?.ok === false ? 'error' : 'ok', parts));
        for (const f of fields) if (f.input.type === 'password') f.input.value = '';
        onDone?.();
      } catch (err) {
        result.replaceChildren(callout('error', err.message));
      } finally {
        btn.disabled = false;
        btn.replaceChildren(label);
      }
    },
  },
  h('div', { class: 'action-text' }, h('div', { class: 'action-label' }, a.label), a.description ? h('p', { class: 'muted small' }, a.description) : null),
  fields.length ? h('div', { class: 'action-fields' }, fields.map((f) => f.el)) : null,
  h('div', { class: 'action-run' }, btn),
  result);
  return form;
}

function createPanel(id, { onClose }) {
  let form = null;
  let detail = null;
  const titleId = uid('panel');
  const closeBtn = h('button', { type: 'button', class: 'icon-btn', 'aria-label': 'Close', onClick: () => onClose() }, icon('x', { size: 20 }));
  const head = h('div', { class: 'drawer-head' });
  const body = h('div', { class: 'drawer-body' }, h('div', { class: 'drawer-loading' }, spinner(), 'Loading…'));
  const panel = h('aside', { class: 'drawer', role: 'dialog', 'aria-modal': 'true', 'aria-labelledby': titleId, tabindex: '-1' }, h('div', { class: 'drawer-top' }, head, closeBtn), body);
  const backdrop = h('div', { class: 'drawer-backdrop', onClick: () => onClose() });
  const wrap = h('div', { class: 'drawer-wrap' }, backdrop, panel);
  const statusLine = h('div', { class: 'drawer-status' });
  let sw = null;

  const titleEl = h('h2', { id: titleId });
  const tileHost = h('span', { class: 'drawer-tile' });
  const swText = h('span', { class: 'muted small' });
  let headBuilt = false;

  function renderHead(p) {
    if (!headBuilt) {
      headBuilt = true;
      sw = switchEl({ checked: p.enabled, label: `${p.name} on or off` });
      sw.input.addEventListener('change', async () => {
        const want = sw.input.checked;
        sw.input.disabled = true;
        statusLine.replaceChildren(spinner('spinner-sm'), want ? 'Starting…' : 'Stopping…');
        const d = await setEnabled(p, want);
        sw.input.disabled = false;
        if (!d) sw.input.checked = !want;
        else apply(d, { keepForm: true });
      });
      head.replaceChildren(
        tileHost,
        h('div', { class: 'drawer-title' }, titleEl, statusLine),
        h('label', { class: 'drawer-switch' }, swText, sw));
    }
    tileHost.replaceChildren(pluginTile(p.icon, 48));
    titleEl.textContent = p.name;
    if (sw.input.disabled) return; // starting or stopping: leave the spinner
    sw.input.checked = !!p.enabled;
    swText.textContent = p.enabled ? 'On' : 'Off';
    const st = p.status || {};
    statusLine.replaceChildren(statusDot(p.enabled ? st.level : 'Idle'), h('span', null, p.enabled ? st.message || 'On' : 'Off'));
  }

  // The body is built once and updated in place, so action results, typed inputs, the form and
  // the tools list's open state survive refreshes.
  const descEl = h('p', { class: 'drawer-desc' });
  const noteHost = h('div');
  const actionsList = h('div', { class: 'actions-list' });
  const actionsSection = h('section', { class: 'drawer-section', hidden: true }, h('h3', { class: 'section-title' }, 'Actions'), actionsList);
  const settingsSection = h('section', { class: 'drawer-section' }, h('h3', { class: 'section-title' }, 'Settings'));
  const toolsSummary = h('span');
  const toolsBody = h('div', { class: 'section-body' });
  const toolsSection = h('details', { class: 'section drawer-tools' }, h('summary', null, icon('chevron-right', { size: 16, cls: 'chev' }), icon('tool', { size: 16 }), toolsSummary), toolsBody);
  const sourceEl = h('p', { class: 'drawer-source muted small' });
  const actionEls = new Map(); // action id → {sig, el}
  let built = false;
  let toolsSig = '';

  async function refreshDetail() {
    refreshStatus();
    try { apply(await get(`/api/plugins/${enc(id)}`), { keepForm: true }); } catch { /* ignore */ }
  }

  function apply(d, { keepForm = false } = {}) {
    detail = d;
    renderHead(d);
    if (!built) {
      built = true;
      body.replaceChildren(descEl, noteHost, actionsSection, settingsSection, toolsSection, sourceEl);
    }
    descEl.textContent = d.description || '';
    descEl.hidden = !d.description;
    const level = levelClass(d.status?.level);
    noteHost.replaceChildren(!d.enabled
      ? callout('info', h('div', null, 'This plugin is off. You can fill in its settings first, then turn it on with the switch above.'))
      : level === 'err' || level === 'warn' ? callout(level === 'err' ? 'error' : 'warn', h('div', null, d.status.message)) : '');

    const actions = d.actions || [];
    actionsSection.hidden = actions.length === 0;
    const seen = new Set();
    actions.forEach((a, i) => {
      seen.add(a.id);
      const sig = JSON.stringify(a);
      let entry = actionEls.get(a.id);
      if (!entry || entry.sig !== sig) {
        const el = actionBlock(d.id, a, refreshDetail);
        if (entry) entry.el.replaceWith(el);
        entry = { sig, el };
        actionEls.set(a.id, entry);
      }
      if (actionsList.children[i] !== entry.el) actionsList.insertBefore(entry.el, actionsList.children[i] || null);
    });
    for (const [aid, entry] of actionEls) {
      if (!seen.has(aid)) {
        entry.el.remove();
        actionEls.delete(aid);
      }
    }

    if (!form) {
      form = createForm({
        schema: d.schema,
        values: d.values,
        saveLabel: 'Save',
        emptyText: 'This plugin has nothing to set up.',
        onSave: async (values) => {
          const fresh = await put(`/api/plugins/${enc(d.id)}/settings`, values);
          refreshStatus();
          setTimeout(() => apply(fresh, { keepForm: true }), 0);
          return { schema: fresh.schema, values: fresh.values };
        },
      });
      settingsSection.append(form.el);
    } else if (!keepForm && !form.isDirty()) {
      form.setData(d.schema, d.values);
    }

    const tools = d.tools || [];
    const tsig = JSON.stringify(tools);
    if (tsig !== toolsSig) {
      toolsSig = tsig;
      toolsSummary.textContent = tools.length ? `${plural(tools.length, 'tool')} Claude can use` : 'No tools';
      toolsBody.replaceChildren(tools.length
        ? h('ul', { class: 'tool-list' }, tools.map((t) => h('li', null,
          h('div', { class: 'tool-name' }, h('code', null, t.name), t.confirm ? h('span', { class: 'badge badge-warn', title: 'Claude asks on the puck before running this' }, 'Asks first') : null),
          t.description ? h('p', { class: 'muted small' }, t.description) : null)))
        : h('p', { class: 'muted' }, 'This plugin doesn’t give Claude any tools (it may offer speech, media or puck controls instead).'));
    }
    sourceEl.replaceChildren(icon('package', { size: 14 }), d.source === 'built in' ? 'Built into the hub' : `From ${d.source}`, ' · id ', h('code', null, d.id));
  }

  async function load() {
    try {
      apply(await get(`/api/plugins/${enc(id)}`));
    } catch (e) {
      titleEl.textContent = 'Plugin';
      if (!headBuilt) head.replaceChildren(titleEl);
      body.replaceChildren(callout('error', e.status === 404 ? 'That plugin isn’t loaded in this hub.' : e.message));
    }
  }

  document.body.append(wrap);
  document.documentElement.classList.add('has-drawer');
  requestAnimationFrame(() => {
    wrap.classList.add('is-open');
    panel.focus({ preventScroll: true });
  });
  const known = pluginById(id);
  if (known) renderHead(known);
  load();

  const onEsc = (e) => {
    if (e.key !== 'Escape' || e.defaultPrevented || document.querySelector('dialog[open]')) return;
    e.preventDefault();
    onClose();
  };
  document.addEventListener('keydown', onEsc);
  const trap = (e) => {
    if (e.key === 'Tab') {
      const items = panel.querySelectorAll('a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), summary, [tabindex]:not([tabindex="-1"])');
      const visible = [...items].filter((x) => x.offsetParent !== null);
      if (!visible.length) return;
      const first = visible[0];
      const last = visible[visible.length - 1];
      if (e.shiftKey && (document.activeElement === first || document.activeElement === panel)) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    }
  };
  wrap.addEventListener('keydown', trap);

  return {
    id,
    statusChanged() {
      const p = pluginById(id);
      if (p && detail) renderHead({ ...detail, ...p });
    },
    close() {
      document.removeEventListener('keydown', onEsc);
      form?.destroy();
      wrap.classList.remove('is-open');
      document.documentElement.classList.remove('has-drawer');
      setTimeout(() => wrap.remove(), 220);
    },
  };
}

// ------------------------------------------------------------------ the page

export default {
  title: 'Plugins',
  mount(root, route) {
    let filter = 'all';
    let kind = route?.query?.get('kind') || '';
    let search = '';
    const busy = new Set();
    const cards = new Map(); // id → {el, sig}
    let panel = null;
    let opener = null;

    const searchInput = h('input', { class: 'input input-search', type: 'search', placeholder: 'Search plugins', 'aria-label': 'Search plugins', autocomplete: 'off' });
    searchInput.addEventListener('input', () => { search = searchInput.value.trim().toLowerCase(); render(true); });
    const filters = h('div', { class: 'filter-chips', role: 'group', 'aria-label': 'Show' });
    const kindHost = h('span');
    const grid = h('div', { class: 'pgrid' });
    const emptyHost = h('div');
    const summary = h('p', { class: 'lede' });

    root.append(
      h('div', { class: 'page-head' },
        h('div', null,
          h('h1', null, 'Plugins'),
          summary)),
      h('div', { class: 'toolbar' }, filters, kindHost, searchInput),
      grid, emptyHost);

    function matches(p) {
      if (filter === 'on' && !p.enabled) return false;
      if (filter === 'off' && p.enabled) return false;
      if (filter === 'attention' && !needsAttention(p)) return false;
      if (kind && !(p.kinds || []).includes(kind)) return false;
      if (search && !`${p.name} ${p.description} ${p.id}`.toLowerCase().includes(search)) return false;
      return true;
    }

    let filtersSig = '';
    function renderFilters(list) {
      const counts = {
        all: list.length,
        on: list.filter((p) => p.enabled).length,
        attention: list.filter(needsAttention).length,
        off: list.filter((p) => !p.enabled).length,
      };
      const sig = JSON.stringify([counts, filter, kind]);
      if (sig === filtersSig) return;
      filtersSig = sig;
      const defs = [['all', 'All'], ['on', 'On'], ['attention', 'Needs attention'], ['off', 'Off']];
      filters.replaceChildren(...defs
        .filter(([k]) => k !== 'attention' || counts.attention > 0 || filter === 'attention')
        .map(([k, label]) => h('button', {
          type: 'button',
          class: ['fchip', k === 'attention' && 'fchip-warn'],
          'aria-pressed': String(filter === k),
          onClick: () => { filter = k; render(true); },
        }, label, h('span', { class: 'fchip-count' }, String(counts[k])))));
      kindHost.replaceChildren(kind
        ? h('button', { type: 'button', class: 'fchip fchip-kind', 'aria-pressed': 'true', title: 'Show all kinds', onClick: () => { kind = ''; render(true); } }, KIND_NAMES[kind] || kind, icon('x', { size: 14 }))
        : '');
    }

    function render(force = false) {
      const s = store.status;
      if (!s) return;
      const list = s.plugins || [];
      const on = list.filter((p) => p.enabled).length;
      const attention = list.filter(needsAttention).length;
      summary.textContent = list.length
        ? `Plugins give Claude abilities: music, heating, the TV, your PC and more. ${on} of ${list.length} are on${attention ? `, ${attention} need${attention === 1 ? 's' : ''} attention` : ''}.`
        : 'Plugins give Claude abilities: music, heating, the TV, your PC and more.';
      renderFilters(list);

      const shown = list.filter(matches);
      const seen = new Set();
      shown.forEach((p, i) => {
        seen.add(p.id);
        const sig = cardSig(p);
        let c = cards.get(p.id);
        if (!c || (c.sig !== sig && !busy.has(p.id)) || force && !c) {
          const el = pluginCard(p, busy);
          if (c) {
            const hadFocus = c.el.contains(document.activeElement);
            const wasSwitch = document.activeElement?.classList.contains('switch-input');
            c.el.replaceWith(el);
            if (hadFocus) (wasSwitch ? el.querySelector('.switch-input') : el.querySelector('a'))?.focus();
          }
          c = { el, sig };
          cards.set(p.id, c);
        }
        if (grid.children[i] !== c.el) grid.insertBefore(c.el, grid.children[i] || null);
      });
      for (const [id, c] of cards) {
        if (!seen.has(id)) {
          c.el.remove();
          cards.delete(id);
        }
      }
      if (!list.length) {
        emptyHost.replaceChildren(h('div', { class: 'card' }, emptyState({ iconName: 'plugins', title: 'No plugins loaded', text: 'The hub didn’t find any plugins. Check the Activity log for errors while it started.' })));
      } else if (!shown.length) {
        emptyHost.replaceChildren(h('div', { class: 'card' }, emptyState({ iconName: 'search', title: 'Nothing matches', text: 'Try another filter or search.' })));
      } else {
        clear(emptyHost);
      }
    }

    function syncPanel(r) {
      const id = r?.rest?.[0] || null;
      if (panel && panel.id !== id) {
        panel.close();
        panel = null;
        if (opener && opener.isConnected) opener.focus({ preventScroll: true });
        else grid.querySelector(`[data-id="${CSS.escape(id || '')}"] a`)?.focus({ preventScroll: true });
      }
      if (id && !panel) {
        opener = document.activeElement;
        panel = createPanel(id, { onClose: () => { location.hash = '#/plugins'; } });
      }
    }

    render(true);
    syncPanel(route);
    const offStatus = on('status', () => panel?.statusChanged());

    return {
      update: () => render(),
      route(r) {
        if (r.query.has('kind')) {
          kind = r.query.get('kind') || '';
          render(true);
        }
        syncPanel(r);
      },
      unmount() {
        offStatus();
        panel?.close();
        panel = null;
      },
    };
  },
};
