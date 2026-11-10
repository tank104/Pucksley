// Pucks: each puck's live state, its settings, Wi-Fi over USB, quick tests, and forgetting it.

import { h, clear, toast, confirmDialog, fmtAgo, emptyState, callout, spinner, uid } from './dom.js';
import { icon } from './icons.js';
import { get, put, post, del, enc } from './api.js';
import { store, on, refreshStatus } from './store.js';
import { createForm, rangeControl, choiceControl } from './forms.js';
import { puckOrb, setOrb, puckState, signalInfo, signalBars, batteryText, batteryIcon, faceName, transportBadges, pendingBanner, howToConnect, puckHubUrl } from './components.js';

function mediaOptions() {
  return [{ value: '', label: 'The household default' }, ...(store.status?.mediaSources || []).map((m) => ({ value: m.id, label: `${m.name} (${m.id.split(':')[0]})` }))];
}

const PUCK_FIELDS = {
  mediaSource: choiceControl(mediaOptions, { subscribe: (fn) => on('status', fn) }),
  speakerVolume: rangeControl({ min: 0, max: 100, unit: '%' }),
  micGain: rangeControl({ min: 0, max: 37, unit: ' dB' }),
  brightness: rangeControl({ min: 10, max: 100, unit: '%' }),
  turnSensitivity: rangeControl({ min: 0.25, max: 4, step: 0.25, format: (n) => `${n}×` }),
};

function fact(label, ...value) {
  return [h('dt', null, label), h('dd', null, value)];
}

function createPuckCard(initial) {
  const id = initial.id;
  let p = initial;
  let form = null;

  const st = puckState(p);
  const orb = puckOrb(st.state, 72, st.label);
  const nameEl = h('h2', { class: 'puck-name' });
  const subEl = h('div', { class: 'puck-sub' });
  const badgesEl = h('div', { class: 'puck-badges' });
  const facts = h('dl', { class: 'facts' });

  // ---- try it
  const sayInput = h('input', { class: 'input', type: 'text', placeholder: 'Hello from the hub!', 'aria-label': 'Something for the puck to say', autocomplete: 'off' });
  const sayBtn = h('button', { type: 'submit', class: 'btn' }, icon('speaker', { size: 16 }), 'Say it');
  const listenBtn = h('button', { type: 'button', class: 'btn' }, icon('mic', { size: 16 }), 'Start listening');
  const tryForm = h('form', {
    class: 'try-row',
    onSubmit: async (e) => {
      e.preventDefault();
      const text = sayInput.value.trim() || sayInput.placeholder;
      sayBtn.disabled = true;
      try {
        await post(`/api/pucks/${enc(id)}/say`, { text });
        toast(`${p.name} is saying it now.`, 'ok', 2500);
      } catch (err) {
        toast(err.status === 404 ? `${p.name} isn’t connected.` : err.message, 'error');
      } finally {
        sayBtn.disabled = !p.connected;
      }
    },
  }, sayInput, sayBtn);
  listenBtn.addEventListener('click', async () => {
    try {
      await post(`/api/pucks/${enc(id)}/listen`);
      toast(`${p.name} is listening: say something to it.`, 'ok', 3500);
    } catch (err) {
      toast(err.status === 404 ? `${p.name} isn’t connected.` : err.message, 'error');
    }
  });

  // ---- Wi-Fi over USB
  const ssid = h('input', { class: 'input', id: uid('ssid'), type: 'text', autocomplete: 'off', spellcheck: 'false', placeholder: 'Your Wi-Fi name' });
  const pass = h('input', { class: 'input', id: uid('pass'), type: 'password', autocomplete: 'new-password', spellcheck: 'false' });
  const passEye = h('button', { type: 'button', class: 'input-addon', 'aria-label': 'Show password', onClick: () => {
    const show = pass.type === 'password';
    pass.type = show ? 'text' : 'password';
    passEye.replaceChildren(icon(show ? 'eye-off' : 'eye', { size: 17 }));
  } }, icon('eye', { size: 17 }));
  const hubUrl = h('input', { class: 'input', id: uid('hub'), type: 'text', autocomplete: 'off', spellcheck: 'false', placeholder: 'Leave empty to find the hub automatically' });
  const useHub = h('button', { type: 'button', class: 'linkish', onClick: () => { hubUrl.value = puckHubUrl(); } }, 'Use this hub’s address');
  const wifiBtn = h('button', { type: 'submit', class: 'btn btn-primary' }, icon('wifi', { size: 16 }), 'Send to the puck');
  const wifiState = h('div', { class: 'wifi-state', 'aria-live': 'polite' });
  const wifiNote = h('div', { class: 'wifi-note' });
  const wifiFields = h('fieldset', { class: 'wifi-fields' },
    h('div', { class: 'field' }, h('label', { class: 'field-label', for: ssid.id }, 'Network name (SSID)'), ssid),
    h('div', { class: 'field' }, h('label', { class: 'field-label', for: pass.id }, 'Password'), h('div', { class: 'input-group' }, pass, passEye)),
    h('div', { class: 'field' }, h('label', { class: 'field-label', for: hubUrl.id }, 'Hub address ', h('span', { class: 'muted' }, '(optional)')), hubUrl,
      h('p', { class: 'field-help' }, 'The puck normally finds the hub by itself. Fill this in if it can’t (for example when the hub runs in Docker). ', useHub)),
    h('div', { class: 'form-row-end' }, wifiState, wifiBtn));
  const wifiForm = h('form', {
    class: 'wifi-form',
    onSubmit: async (e) => {
      e.preventDefault();
      if (!ssid.value.trim()) {
        ssid.focus();
        toast('Enter the Wi-Fi network’s name.', 'info');
        return;
      }
      wifiBtn.disabled = true;
      try {
        const body = { ssid: ssid.value.trim(), password: pass.value };
        if (hubUrl.value.trim()) body.hubUrl = hubUrl.value.trim();
        await post(`/api/pucks/${enc(id)}/wifi`, body);
        wifiState.replaceChildren(spinner('spinner-sm'), 'Sent. The puck is saving it and joining the network…');
        toast(`Sent the Wi-Fi details to ${p.name}.`, 'ok');
      } catch (err) {
        wifiState.replaceChildren(icon('alert', { size: 15 }), err.message);
      } finally {
        wifiBtn.disabled = false;
      }
    },
  }, wifiNote, wifiFields);
  const wifiSection = h('details', { class: 'section' },
    h('summary', null, icon('chevron-right', { size: 16, cls: 'chev' }), icon('usb', { size: 16 }), h('span', null, 'Wi-Fi setup over USB')),
    h('div', { class: 'section-body' }, wifiForm));

  // ---- settings
  const settingsHost = h('div', { class: 'puck-settings-body' }, h('p', { class: 'muted' }, spinner('spinner-sm'), ' Loading settings…'));
  async function loadSettings() {
    try {
      const d = await get(`/api/pucks/${enc(id)}`);
      if (form) {
        form.setData(d.schema, d.values);
        return;
      }
      form = createForm({
        schema: d.schema,
        values: d.values,
        custom: PUCK_FIELDS,
        saveLabel: 'Save settings',
        onSave: async (values) => {
          await put(`/api/pucks/${enc(id)}`, values);
          const fresh = await get(`/api/pucks/${enc(id)}`);
          refreshStatus();
          return { schema: fresh.schema, values: fresh.values };
        },
      });
      settingsHost.replaceChildren(form.el);
    } catch (err) {
      settingsHost.replaceChildren(callout('error', `Couldn’t load this puck’s settings: ${err.message}`, ' ', h('button', { type: 'button', class: 'linkish', onClick: loadSettings }, 'Try again')));
    }
  }
  loadSettings();

  const forgetBtn = h('button', { type: 'button', class: 'btn btn-danger btn-sm' }, icon('trash', { size: 15 }), 'Forget this puck');
  forgetBtn.addEventListener('click', async () => {
    const ok = await confirmDialog({
      title: `Forget ${p.name}?`,
      message: 'It will be unpaired from this hub. To use it again, it has to reconnect and be approved (or be plugged in by USB).',
      confirmLabel: 'Forget it',
      danger: true,
    });
    if (!ok) return;
    try {
      await del(`/api/pucks/${enc(id)}`);
      toast(`Forgot ${p.name}.`, 'ok');
      refreshStatus();
    } catch (err) {
      toast(err.message, 'error');
    }
  });

  const el = h('article', { class: 'card puck-card', id: `puck-${id}`, 'aria-labelledby': `puck-${id}-name` },
    h('div', { class: 'puck-card-head' },
      orb,
      h('div', { class: 'puck-head-text' }, nameEl, subEl),
      badgesEl),
    h('div', { class: 'puck-card-body' },
      h('div', { class: 'puck-col' },
        facts,
        h('div', { class: 'section section-plain' },
          h('h3', { class: 'section-title' }, 'Try it'),
          tryForm,
          h('div', { class: 'try-more' }, listenBtn)),
        wifiSection,
        h('div', { class: 'puck-danger' }, forgetBtn)),
      h('div', { class: 'puck-col puck-settings' },
        h('h3', { class: 'section-title' }, 'Settings'),
        settingsHost)));
  nameEl.id = `puck-${id}-name`;

  function update(next) {
    p = next;
    const s = puckState(p);
    setOrb(orb, s.state, s.label);
    nameEl.textContent = p.name || p.deviceName || p.id;
    subEl.textContent = [p.room || 'No room set', p.connected ? s.label : `Offline · last seen ${fmtAgo(p.lastSeen)}`].join(' · ');
    badgesEl.replaceChildren(...transportBadges(p));
    el.classList.toggle('is-offline', !p.connected);

    const sig = signalInfo(p.rssi);
    const rows = [];
    if (p.connected) {
      rows.push(fact('Address', p.ip || p.address || '—'));
      if (p.ssid || sig) rows.push(fact('Wi-Fi', p.ssid || '—', sig ? h('span', { class: 'sig-inline' }, signalBars(sig.bars), `${sig.label} (${p.rssi} dBm)`) : null));
      rows.push(fact('Battery', batteryIcon(p), batteryText(p)));
      rows.push(fact('Assistant', s.label));
      rows.push(fact('Screen', faceName(p.face)));
      rows.push(fact('Media', p.mediaSource || 'Nothing to follow yet'));
      if (p.directMode) rows.push(fact('Without the hub', p.directMode === 'Off' ? 'Off (set it up on the Assistant page)' : p.directMode));
      rows.push(fact('Firmware', p.firmware || '—'));
    } else {
      rows.push(fact('Last seen', fmtAgo(p.lastSeen)));
    }
    rows.push(fact('Device', h('span', null, p.deviceName || '—', ' ', h('code', { class: 'muted small' }, p.id))));
    facts.replaceChildren(...rows.flat());

    sayInput.disabled = !p.connected;
    sayBtn.disabled = !p.connected;
    listenBtn.disabled = !p.connected;

    const usb = p.connected && (p.transports || []).includes('usb');
    wifiFields.disabled = !usb;
    wifiNote.replaceChildren(usb
      ? h('p', { class: 'muted small' }, 'Send your Wi-Fi details down the USB cable. The puck saves them and joins the network; then it can be unplugged.')
      : callout('info', h('div', null, 'Plug this puck into the computer running the hub with a USB data cable to use this.',
        p.connected ? ' It’s on Wi-Fi now, so you only need this to move it to a different network.' : '')));
    if (usb && !p.ssid && !wifiSection.dataset.opened) {
      wifiSection.open = true;
      wifiSection.dataset.opened = '1';
    }
  }

  function wifiProgress(state) {
    const s = String(state || '').toLowerCase();
    const map = {
      saved: [spinner('spinner-sm'), 'Saved. Joining the network…'],
      connecting: [spinner('spinner-sm'), 'Connecting to Wi-Fi…'],
      connected: [icon('check-circle', { size: 16 }), 'Connected to Wi-Fi.'],
      failed: [icon('alert', { size: 16 }), 'It couldn’t join that network. Check the name and password.'],
    };
    if (map[s]) {
      wifiState.replaceChildren(...map[s]);
      wifiState.dataset.state = s;
    }
  }

  update(p);
  return {
    el,
    update,
    wifiProgress,
    get name() { return p.name; },
    destroy: () => form?.destroy(),
  };
}

export default {
  title: 'Pucks',
  mount(root, route) {
    const usbNote = h('div');
    const pendingHost = h('div');
    const cardsHost = h('div', { class: 'puck-cards' });
    const emptyHost = h('div');
    root.append(
      h('div', { class: 'page-head' },
        h('div', null,
          h('h1', null, 'Pucks'),
          h('p', { class: 'lede' }, 'Each puck is a round screen, speaker and microphone that talks to this hub. Name it and say which room it’s in so “turn it down” means the right speaker.'))),
      pendingHost, usbNote, cardsHost, emptyHost);

    const cards = new Map();
    let focusId = route?.rest?.[0] || null;
    let usbPorts = [];
    let lastPending = '';

    function sync(s) {
      const pendSig = JSON.stringify((s.pending || []).map((p) => [p.id, p.code]));
      if (pendSig !== lastPending) {
        lastPending = pendSig;
        pendingHost.replaceChildren(pendingBanner(s.pending));
      }
      const list = s.pucks || [];
      const seen = new Set();
      for (const p of list) {
        seen.add(p.id);
        let c = cards.get(p.id);
        if (!c) {
          c = createPuckCard(p);
          cards.set(p.id, c);
        } else {
          c.update(p);
        }
      }
      for (const [cid, c] of cards) {
        if (!seen.has(cid)) {
          c.destroy();
          c.el.remove();
          cards.delete(cid);
        }
      }
      // Connected first, then by name; keep DOM order stable otherwise
      const order = [...list].sort((a, b) => Number(b.connected) - Number(a.connected) || String(a.name).localeCompare(String(b.name)));
      order.forEach((p, i) => {
        const el = cards.get(p.id).el;
        if (cardsHost.children[i] !== el) cardsHost.insertBefore(el, cardsHost.children[i] || null);
      });
      if (!list.length) {
        if (!emptyHost.firstChild) {
          emptyHost.append(h('div', { class: 'card' },
            emptyState({ iconName: 'puck', title: 'No pucks yet', text: (s.pending || []).length ? 'Approve the waiting puck above and it will appear here.' : 'Here’s how to connect your first one.' }),
            howToConnect()));
        }
      } else {
        clear(emptyHost);
      }
      const usbUnclaimed = usbPorts.length && !list.some((p) => p.connected && (p.transports || []).includes('usb'));
      usbNote.replaceChildren(usbUnclaimed
        ? callout('info', h('div', null, h('strong', null, `Something that looks like a puck is plugged in by USB (${usbPorts.join(', ')})`), ', but it hasn’t said hello yet. If it’s a puck, give it a few seconds; if nothing happens, close any serial monitor or flasher using that port, or unplug it and plug it back in.'))
        : '');
      if (focusId && cards.has(focusId)) {
        const el = cards.get(focusId).el;
        focusId = null;
        requestAnimationFrame(() => {
          el.scrollIntoView({ behavior: 'smooth', block: 'start' });
          el.classList.add('is-flash');
          setTimeout(() => el.classList.remove('is-flash'), 1600);
        });
      }
    }

    const offLog = on('log', (ev) => {
      const m = /^(.*) Wi-Fi: (\w+)$/.exec(ev.message || '');
      if (!m) return;
      for (const c of cards.values()) if (c.name === m[1]) c.wifiProgress(m[2]);
    });

    const pollUsb = async () => {
      try {
        const ports = await get('/api/usb-ports', { timeout: 8000 });
        const next = Array.isArray(ports) ? ports : [];
        if (JSON.stringify(next) !== JSON.stringify(usbPorts)) {
          usbPorts = next;
          if (store.status) sync(store.status);
        }
      } catch { /* ignore */ }
    };
    pollUsb();
    const usbTimer = setInterval(pollUsb, 5000);

    if (store.status) sync(store.status);
    return {
      update: sync,
      route(r) {
        focusId = r.rest[0] || null;
        if (store.status) sync(store.status);
      },
      unmount() {
        offLog();
        clearInterval(usbTimer);
        for (const c of cards.values()) c.destroy();
      },
    };
  },
};
