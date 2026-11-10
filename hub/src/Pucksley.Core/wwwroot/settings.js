// The hub-wide settings pages: Assistant, Voice and Household (with the hub's name).

import { h, toast, spinner, callout, copyButton, fmtAgo, uid, markdown } from './dom.js';
import { icon } from './icons.js';
import { get, put, post } from './api.js';
import { store, on, refreshStatus, setSection } from './store.js';
import { createForm, textControl, choiceControl, rangeControl } from './forms.js';
import { pluginForEngine, statusDot } from './components.js';
import { playBlob, unlockAudio } from './audio.js';

/** Loads a settings section into a form inside `host`. Returns {form()} so pages can reach it. */
function sectionForm(host, section, { custom = {}, saveLabel = 'Save', onSaved } = {}) {
  let form = null;
  host.replaceChildren(h('div', { class: 'loading-row' }, spinner(), 'Loading…'));
  const load = async () => {
    try {
      const d = await get(`/api/settings/${section}`);
      setSection(section, d);
      form = createForm({
        schema: d.schema,
        values: d.values,
        custom,
        saveLabel,
        onSave: async (values) => {
          const r = await put(`/api/settings/${section}`, values);
          setSection(section, r);
          refreshStatus();
          onSaved?.(r);
          return r;
        },
      });
      host.replaceChildren(form.el);
    } catch (e) {
      host.replaceChildren(callout('error', h('div', null, `Couldn’t load these settings: ${e.message} `, h('button', { type: 'button', class: 'linkish', onClick: load }, 'Try again'))));
    }
  };
  load();
  return {
    get form() { return form; },
    destroy: () => form?.destroy(),
  };
}

function pageHead(title, lede) {
  return h('div', { class: 'page-head' }, h('div', null, h('h1', null, title), h('p', { class: 'lede' }, lede)));
}

// ------------------------------------------------------------------ Assistant

export const assistantPage = {
  title: 'Assistant',
  mount(root) {
    const host = h('div');
    const out = h('div', { class: 'test-out', 'aria-live': 'polite' });
    const testBtn = h('button', { type: 'button', class: 'btn' }, icon('sparkle', { size: 16 }), 'Say hello to Claude');
    testBtn.addEventListener('click', async () => {
      testBtn.disabled = true;
      out.replaceChildren(h('div', { class: 'ask-thinking' }, spinner(), 'Asking Claude…'));
      try {
        const r = await post('/api/chat', { text: 'This is a quick connection test from the hub’s settings page. Reply with one short, friendly sentence.', session: 'test' });
        out.replaceChildren(r.failed
          ? callout('error', r.text)
          : h('div', { class: 'ask-a md' }, markdown(r.text)));
      } catch (e) {
        out.replaceChildren(callout('error', e.message));
      } finally {
        testBtn.disabled = false;
      }
    });
    const readyEl = h('div', { class: 'side-status' });
    const renderStatus = () => {
      const a = store.status?.assistant;
      if (!a) return;
      readyEl.replaceChildren(statusDot(a.ready ? 'Ok' : 'Warning'), h('span', null, a.ready ? `Ready · ${a.model} · ${a.toolCount} tools from plugins` : 'Needs an API key'));
    };
    renderStatus();

    const directHost = h('div');
    root.append(
      pageHead('Assistant', 'How Claude behaves: the API key, the model, its name and personality.'),
      h('div', { class: 'settings-layout' },
        h('div', { class: 'settings-main' },
          h('section', { class: 'card form-card' }, host),
          h('section', { class: 'card form-card' },
            h('h2', { class: 'card-title' }, 'When the hub is off'),
            h('p', { class: 'muted small' }, 'Pucks on Wi-Fi can still answer questions without the hub: they call Claude and a cloud speech service themselves, with keys the hub gives them. Home control, music and the PC need the hub.'),
            directHost)),
        h('aside', { class: 'settings-side' },
          h('section', { class: 'card' },
            h('h2', { class: 'card-title' }, 'Check it works'),
            readyEl,
            h('p', { class: 'muted small' }, 'After saving the key, send a quick message to make sure Claude answers.'),
            testBtn, out),
          h('section', { class: 'card' },
            h('h2', { class: 'card-title' }, 'Getting a key'),
            h('ol', { class: 'mini-steps' },
              h('li', null, 'Sign in at ', h('a', { href: 'https://console.anthropic.com/settings/keys', target: '_blank', rel: 'noopener noreferrer' }, 'console.anthropic.com'), '.'),
              h('li', null, 'Create a key under ', h('strong', null, 'API keys'), '.'),
              h('li', null, 'Paste it here and save. It’s stored encrypted on the hub.'))))));
    const sf = sectionForm(host, 'assistant', { onSaved: () => setTimeout(renderStatus, 300) });
    const directSf = sectionForm(directHost, 'direct');
    return {
      update: renderStatus,
      unmount() {
        sf.destroy();
        directSf.destroy();
      },
    };
  },
};

// ------------------------------------------------------------------ Voice

const LANGUAGES = ['en-NZ', 'en-AU', 'en-GB', 'en-US', 'en-IE', 'en-CA', 'en-ZA', 'en-IN', 'mi-NZ', 'de-DE', 'fr-FR', 'es-ES', 'es-MX', 'it-IT', 'nl-NL', 'pt-BR', 'pt-PT', 'sv-SE', 'da-DK', 'nb-NO', 'fi-FI', 'pl-PL', 'ja-JP', 'ko-KR', 'zh-CN', 'zh-TW', 'hi-IN'];

function speechPlugins(kind) {
  const want = kind === 'stt' ? 'speech-to-text' : 'voice';
  return (store.status?.plugins || []).filter((p) => (p.kinds || []).includes(want));
}

function engineOptions(kind) {
  const engines = (kind === 'stt' ? store.status?.voice?.sttEngines : store.status?.voice?.ttsEngines) || [];
  const opts = [{ value: '', label: kind === 'stt' ? 'Automatic: the first engine that’s ready' : 'Automatic: the best voice that’s ready' }];
  if (kind === 'tts') opts.push({ value: 'none', label: 'None: show answers as text only' });
  for (const e of engines) opts.push({ value: e.id, label: `${e.name}${e.ready ? '' : ' (needs setting up)'}` });
  return opts;
}

function pluginLink(p, label) {
  return h('a', { href: `#/plugins/${encodeURIComponent(p.id)}` }, label || p.name);
}

function enginePicker(kind, getForm) {
  return (def, value, ctx) => {
    const note = h('div', { class: 'engine-note', 'aria-live': 'polite' });
    let noteSig = null;
    const describe = (v) => {
      const s = store.status;
      if (!s) return;
      const engines = (kind === 'stt' ? s.voice?.sttEngines : s.voice?.ttsEngines) || [];
      const effective = kind === 'stt' ? s.voice?.stt : s.voice?.tts;
      const sig = JSON.stringify([v, engines, effective, speechPlugins(kind).map((p) => [p.id, p.name, p.enabled])]);
      if (sig === noteSig) return;
      noteSig = sig;
      const parts = [];
      const val = String(v || '');
      if (val === '') {
        parts.push(h('div', { class: effective?.ready ? 'note-ok' : 'note-warn' }, icon(effective?.ready ? 'check-circle' : 'alert', { size: 16 }),
          h('span', null, effective ? `Right now that’s ${effective.name}${effective.ready ? '.' : ', which isn’t set up yet.'}` : 'Nothing is ready yet.')));
      } else if (val.toLowerCase() === 'none') {
        parts.push(h('div', { class: 'note-info' }, icon('info', { size: 16 }), h('span', null, 'The puck shows answers on its screen and stays quiet.')));
      } else {
        const e = engines.find((x) => x.id.toLowerCase() === val.toLowerCase());
        if (!e) {
          parts.push(h('div', { class: 'note-warn' }, icon('alert', { size: 16 }), h('span', null, `“${val}” isn’t available: its plugin is off or missing. `, h('a', { href: `#/plugins?kind=${kind === 'stt' ? 'speech-to-text' : 'voice'}` }, 'Find it on the Plugins page'), '.')));
        } else {
          const p = pluginForEngine(e, kind);
          parts.push(e.ready
            ? h('div', { class: 'note-ok' }, icon('check-circle', { size: 16 }), h('span', null, 'Ready.', p ? [' ', pluginLink(p, `${p.name} settings`)] : ''))
            : h('div', { class: 'note-warn' }, icon('alert', { size: 16 }), h('span', null, `${e.name} needs setting up first. `, p ? pluginLink(p, `Set up ${p.name}`) : h('a', { href: '#/plugins' }, 'Open Plugins'), '.')));
        }
      }
      const off = speechPlugins(kind).filter((p) => !p.enabled);
      if (off.length) {
        parts.push(h('div', { class: 'note-more' }, 'Also available (turn on first): ', off.map((p, i) => [i ? ', ' : '', pluginLink(p)]), '.'));
      }
      if (!engines.length && !off.length) {
        parts.push(h('div', { class: 'note-more' }, 'No speech plugins are loaded.'));
      }
      note.replaceChildren(...parts);
    };
    const base = choiceControl(() => engineOptions(kind), {
      subscribe: (fn) => on('status', fn),
      onPick: describe,
      missingLabel: (v) => `${v} (not available)`,
    })(def, value, ctx);

    let sample = null;
    if (kind === 'tts') {
      const text = h('input', { class: 'input', type: 'text', value: 'Hello! This is how I sound. Kia ora!', 'aria-label': 'Sample text', autocomplete: 'off' });
      const btn = h('button', { type: 'button', class: 'btn' }, icon('play', { size: 15 }), h('span', null, 'Play a sample'));
      const label = btn.lastChild;
      const refreshLabel = () => {
        const none = base.select.value.toLowerCase() === 'none';
        btn.disabled = none;
        text.disabled = none;
        label.textContent = none ? 'No voice (text only)' : getForm()?.isDirty() ? 'Save and play a sample' : 'Play a sample';
      };
      base.select.addEventListener('change', refreshLabel);
      btn.addEventListener('click', async () => {
        unlockAudio();
        const form = getForm();
        if (form?.isDirty()) {
          const ok = await form.save();
          if (!ok) return;
        }
        refreshLabel();
        btn.disabled = true;
        label.textContent = 'Making speech…';
        try {
          const wav = await post('/api/voice/speak', { text: text.value.trim() || 'Hello!' });
          label.textContent = 'Playing…';
          await playBlob(wav);
        } catch (e) {
          toast(e.message, 'error', 7000);
        } finally {
          btn.disabled = false;
          refreshLabel();
        }
      });
      sample = h('div', { class: 'sample-row' }, text, btn);
      sample._refresh = refreshLabel;
      setTimeout(refreshLabel, 0);
    }
    const el = h('div', { class: 'engine-pick' }, base.el, note, sample);
    return { ...base, el, refreshSample: () => sample?._refresh() };
  };
}

function languageControl(def, value, ctx) {
  const base = textControl(def, value, ctx);
  const listId = uid('langs');
  base.input.setAttribute('list', listId);
  const browser = navigator.language && /-/.test(navigator.language) ? navigator.language : null;
  const use = browser
    ? h('button', { type: 'button', class: 'linkish', onClick: () => { base.set(browser); ctx.changed(); } }, `Use this device’s (${browser})`)
    : null;
  return { ...base, el: h('div', { class: 'with-hint' }, base.el, h('datalist', { id: listId }, LANGUAGES.map((l) => h('option', { value: l }))), use) };
}

export const voicePage = {
  title: 'Voice',
  mount(root) {
    const host = h('div');
    let sf = null;
    const getForm = () => sf?.form;
    const custom = {
      sttEngine: enginePicker('stt', getForm),
      ttsEngine: enginePicker('tts', getForm),
      language: languageControl,
      vadSensitivity: rangeControl({ min: 1.5, max: 6, step: 0.25, format: (n) => n.toFixed(2).replace(/0$/, '') }),
    };
    const pluginsCard = h('section', { class: 'card' });
    let pluginsSig = '';
    const renderPlugins = () => {
      const list = (store.status?.plugins || []).filter((p) => (p.kinds || []).some((k) => k === 'speech-to-text' || k === 'voice'));
      const sig = JSON.stringify(list.map((p) => [p.id, p.name, p.enabled, p.status?.level, p.status?.message, p.kinds]));
      if (sig === pluginsSig) return;
      pluginsSig = sig;
      pluginsCard.replaceChildren(
        h('h2', { class: 'card-title' }, 'Speech plugins'),
        list.length
          ? h('ul', { class: 'mini-plugins' }, list.map((p) => h('li', null,
            h('a', { href: `#/plugins/${encodeURIComponent(p.id)}` }, statusDot(p.enabled ? p.status?.level : 'Idle'), h('strong', null, p.name)),
            h('span', { class: 'muted small' }, p.enabled ? p.status?.message || 'On' : 'Off'),
            h('span', { class: 'mini-kinds' }, (p.kinds || []).map((k) => (k === 'voice' ? 'voice' : k === 'speech-to-text' ? 'hearing' : k)).join(' + ')))))
          : h('p', { class: 'muted small' }, 'No speech plugins are loaded.'),
        h('p', { class: 'muted small' }, 'Engines come from these plugins. Turn one on and set it up, then choose it here.'));
    };
    renderPlugins();
    root.append(
      pageHead('Voice', 'How the hub hears you and how it answers. Engines come from the speech plugins.'),
      h('div', { class: 'settings-layout' },
        h('section', { class: 'card form-card' }, host),
        h('aside', { class: 'settings-side' },
          h('section', { class: 'card' },
            h('h2', { class: 'card-title' }, 'Try the whole thing'),
            h('p', { class: 'muted small' }, 'Hold the microphone on the Talk page and ask something: your voice goes through speech recognition, Claude, and back out as speech, all without a puck.'),
            h('a', { class: 'btn', href: '#/talk' }, icon('mic', { size: 16 }), 'Open Talk')),
          pluginsCard)));
    sf = sectionForm(host, 'voice', { custom, onSaved: () => getForm()?.field('ttsEngine')?.refreshSample?.() });
    const offSaved = on('section', ({ name }) => { if (name === 'voice') setTimeout(() => getForm()?.field('ttsEngine')?.refreshSample?.(), 0); });
    return {
      update: renderPlugins,
      unmount() {
        offSaved();
        sf.destroy();
      },
    };
  },
};

// ------------------------------------------------------------------ Household

function timeZoneControl(def, value, ctx) {
  const base = textControl(def, value, ctx);
  const listId = uid('tz');
  base.input.setAttribute('list', listId);
  let zones = [];
  try { zones = Intl.supportedValuesOf ? Intl.supportedValuesOf('timeZone') : []; } catch { zones = []; }
  let browserTz = '';
  try { browserTz = Intl.DateTimeFormat().resolvedOptions().timeZone || ''; } catch { browserTz = ''; }
  const use = browserTz
    ? h('button', { type: 'button', class: 'linkish', onClick: () => { base.set(browserTz); ctx.changed(); } }, `Use this device’s time zone (${browserTz})`)
    : null;
  return { ...base, el: h('div', { class: 'with-hint' }, base.el, h('datalist', { id: listId }, zones.map((z) => h('option', { value: z }))), use) };
}

function mediaSourceOptions() {
  return [
    { value: '', label: 'Automatic' },
    ...(store.status?.mediaSources || []).map((m) => ({ value: m.id, label: `${m.name} (${m.id.split(':')[0]})` })),
  ];
}

function latitudeControl(getForm) {
  return (def, value, ctx) => {
    const input = h('input', { id: ctx.id(def.key), class: 'input input-num', type: 'text', inputmode: 'decimal', autocomplete: 'off', placeholder: def.placeholder || null });
    input.addEventListener('input', ctx.changed);
    const set = (v) => { input.value = v == null ? '' : String(v); };
    set(value);
    const canLocate = window.isSecureContext && 'geolocation' in navigator;
    const locate = canLocate ? h('button', { type: 'button', class: 'linkish', onClick: () => {
      locate.textContent = 'Finding you…';
      navigator.geolocation.getCurrentPosition((pos) => {
        set(Number(pos.coords.latitude.toFixed(4)));
        const lon = getForm()?.field('longitude');
        lon?.set(Number(pos.coords.longitude.toFixed(4)));
        ctx.changed();
        locate.textContent = 'Use this device’s location';
      }, (err) => {
        toast(`Couldn’t get the location: ${err.message}`, 'error');
        locate.textContent = 'Use this device’s location';
      }, { timeout: 15000 });
    } }, 'Use this device’s location') : null;
    return {
      id: input.id,
      el: h('div', { class: 'with-hint' }, input, locate),
      get() {
        const s = input.value.trim().replace(',', '.');
        if (s === '') return null;
        const n = Number(s);
        return Number.isFinite(n) ? n : s;
      },
      set,
      validate() {
        const s = input.value.trim().replace(',', '.');
        if (s === '') return null;
        const n = Number(s);
        if (!Number.isFinite(n)) return 'That isn’t a number.';
        if (n < -90 || n > 90) return 'Latitude is between -90 and 90.';
        return null;
      },
      focus: () => input.focus(),
      describe: (ids) => input.setAttribute('aria-describedby', ids),
      invalid: (on) => input.setAttribute('aria-invalid', on ? 'true' : 'false'),
    };
  };
}

export const householdPage = {
  title: 'Household',
  mount(root) {
    const hubHost = h('div');
    const homeHost = h('div');
    const infoHost = h('div', { class: 'hub-info' });
    const sharingHost = h('div');
    const shareInfo = h('div', { class: 'hub-info' });
    let homeSf = null;
    const getHomeForm = () => homeSf?.form;

    let infoSig = '';
    const renderInfo = () => {
      const hub = store.status?.hub;
      if (!hub) return;
      const sig = JSON.stringify([hub.version, hub.lanUrl, hub.os, hub.dataDir, hub.adminUrl, fmtAgo(hub.startedAt)]);
      if (sig === infoSig) return;
      infoSig = sig;
      const rows = [
        ['Version', hub.version || '—'],
        ['On your network', hub.lanUrl ? h('code', null, hub.lanUrl) : '—'],
        ['Running on', hub.os || '—'],
        ['Started', fmtAgo(hub.startedAt)],
      ];
      if (hub.dataDir) rows.push(['Data folder', h('code', null, hub.dataDir)]);
      const parts = [h('dl', { class: 'facts facts-tight' }, rows.flatMap(([k, v]) => [h('dt', null, k), h('dd', null, v)]))];
      if (hub.adminUrl) {
        parts.push(h('div', { class: 'admin-link' },
          h('h3', { class: 'section-title' }, 'Open from another device'),
          h('p', { class: 'muted small' }, 'This link includes the hub’s admin key. Open it once on your phone or laptop (same Wi-Fi) and that browser stays signed in.'),
          h('div', { class: 'url-box' }, h('code', { class: 'url-code' }, hub.adminUrl), copyButton(hub.adminUrl))));
      }
      infoHost.replaceChildren(...parts);
    };
    renderInfo();

    // Lending this hub's tools to another one (the PC's controls to the hub on the NAS)
    const renderShare = async () => {
      try {
        const d = await get('/api/sharing');
        if (!d.shareTools) {
          shareInfo.replaceChildren(h('p', { class: 'muted small' },
            'With sharing on, a hub elsewhere (on the NAS, say) can use this one’s tools: on that hub, add an MCP server with the address and key that appear here.'));
          return;
        }
        const newKey = h('button', { type: 'button', class: 'linkish' }, 'Make a new key');
        newKey.addEventListener('click', async () => {
          await post('/api/sharing/new-key', {});
          renderShare();
        });
        shareInfo.replaceChildren(
          h('h3', { class: 'section-title' }, 'On the other hub'),
          h('p', { class: 'muted small' }, 'Plugins → MCP servers → add a server: Name ', h('code', null, d.serverName), ', Transport ', h('code', null, 'http'), ', and these two:'),
          h('div', { class: 'url-box' }, h('code', { class: 'url-code' }, d.url), copyButton(d.url)),
          h('div', { class: 'url-box' }, h('code', { class: 'url-code' }, d.header), copyButton(d.header)),
          h('p', { class: 'muted small' }, 'Its Claude then sees this hub’s tools (as mcp_', d.serverName, '_…). Anything that asks first here asks on that hub’s puck. ', newKey));
      } catch (e) {
        shareInfo.replaceChildren(callout('error', e.message));
      }
    };

    root.append(
      pageHead('Household', 'Facts about your home that Claude and the plugins use, and this hub’s name.'),
      h('div', { class: 'settings-layout' },
        h('div', { class: 'settings-main' },
          h('section', { class: 'card form-card' }, h('h2', { class: 'card-title' }, 'Your home'), homeHost)),
        h('aside', { class: 'settings-side' },
          h('section', { class: 'card' }, h('h2', { class: 'card-title' }, 'This hub'), hubHost, infoHost),
          h('section', { class: 'card' },
            h('h2', { class: 'card-title' }, 'More than one hub'),
            h('p', { class: 'muted small' }, 'Run the main hub on the NAS for the pucks, and this one on the PC just for the PC’s controls: turn off pucks here and share the tools.'),
            sharingHost, shareInfo))));

    const hubSf = sectionForm(hubHost, 'hub', { saveLabel: 'Save name' });
    const sharingSf = sectionForm(sharingHost, 'sharing', { onSaved: renderShare });
    renderShare();
    homeSf = sectionForm(homeHost, 'household', {
      custom: {
        timeZone: timeZoneControl,
        defaultMediaSource: choiceControl(mediaSourceOptions, { subscribe: (fn) => on('status', fn) }),
        latitude: latitudeControl(getHomeForm),
      },
    });
    return {
      update: renderInfo,
      unmount() {
        hubSf.destroy();
        homeSf.destroy();
        sharingSf.destroy();
      },
    };
  },
};
