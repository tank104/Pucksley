// Home: the setup checklist, pucks waiting for approval, the pucks, and a quick question box.

import { h, plural, storage, fmtAgo, toast, markdown, copyButton, spinner } from './dom.js';
import { icon } from './icons.js';
import { get, post } from './api.js';
import { store, on, loadSection } from './store.js';
import { pendingBanner, puckTile, howToConnect, statusDot, levelClass, pluginForEngine, activityRows } from './components.js';
import { playBlob, unlockAudio } from './audio.js';

function isSpeechOnly(p) {
  const k = p.kinds || [];
  return k.length > 0 && k.every((x) => x === 'speech-to-text' || x === 'voice');
}

async function playSample(btn) {
  unlockAudio();
  const label = btn.textContent;
  btn.disabled = true;
  btn.replaceChildren(spinner('spinner-sm'), 'Making speech…');
  try {
    const wav = await post('/api/voice/speak', { text: `Hi! I'm ${store.status?.hub?.name ? `your assistant on ${store.status.hub.name}` : 'your assistant'}. This is how I sound.` });
    btn.replaceChildren(icon('speaker', { size: 15 }), 'Playing…');
    await playBlob(wav);
  } catch (e) {
    toast(e.message, 'error');
  } finally {
    btn.disabled = false;
    btn.replaceChildren(icon('play', { size: 15 }), label);
  }
}

function buildSteps(s, usbPorts) {
  const voice = store.sections.voice?.values || {};
  const house = store.sections.household?.values || {};
  const steps = [];

  // 1. Claude
  const a = s.assistant || {};
  steps.push({
    id: 'claude',
    title: 'Connect Claude',
    done: !!a.ready,
    detail: a.ready
      ? `Ready, using ${a.model}.`
      : ['Claude is the brain. Paste an Anthropic API key (from ', h('a', { href: 'https://console.anthropic.com/settings/keys', target: '_blank', rel: 'noopener noreferrer' }, 'console.anthropic.com'), ' → API keys).'],
    action: { label: a.ready ? 'Assistant settings' : 'Add your API key', href: '#/assistant' },
  });

  // 2. Speech recognition
  const stt = s.voice?.stt;
  const sttEngines = s.voice?.sttEngines || [];
  let sttDetail;
  let sttAction = { label: 'Choose an engine', href: '#/voice' };
  if (stt?.ready) {
    sttDetail = `Using ${stt.name}.`;
    sttAction = { label: 'Voice settings', href: '#/voice' };
  } else if (!sttEngines.length) {
    sttDetail = 'Nothing turns speech into text yet. Turn on a speech plugin (local Whisper, OpenAI, Azure…), then pick it on the Voice page.';
    sttAction = { label: 'Find speech plugins', href: '#/plugins?kind=speech-to-text' };
  } else if (stt && !stt.ready) {
    const p = pluginForEngine(stt, 'stt');
    sttDetail = `${stt.name} is chosen but isn’t set up yet.`;
    sttAction = p ? { label: `Set up ${p.name}`, href: `#/plugins/${encodeURIComponent(p.id)}` } : { label: 'Voice settings', href: '#/voice' };
  } else {
    sttDetail = 'Choose how the hub turns speech into text.';
  }
  steps.push({ id: 'stt', title: 'Choose speech recognition', done: !!stt?.ready, detail: sttDetail, action: sttAction });

  // 3. Voice
  const tts = s.voice?.tts;
  const textOnly = String(voice.ttsEngine || '').toLowerCase() === 'none';
  const ttsEngines = s.voice?.ttsEngines || [];
  let ttsDetail;
  let ttsAction = { label: 'Choose a voice', href: '#/voice' };
  let ttsExtra = null;
  if (tts?.ready) {
    ttsDetail = `Speaking with ${tts.name}.`;
    ttsAction = { label: 'Voice settings', href: '#/voice' };
    const b = h('button', { type: 'button', class: 'btn btn-sm' }, icon('play', { size: 15 }), 'Hear it');
    b.addEventListener('click', () => playSample(b));
    ttsExtra = b;
  } else if (textOnly) {
    ttsDetail = 'Text only: answers appear on the puck’s screen without speech.';
    ttsAction = { label: 'Voice settings', href: '#/voice' };
  } else if (!ttsEngines.length) {
    ttsDetail = 'No voice yet. Turn on a voice plugin, or choose “None” on the Voice page to keep answers as text.';
    ttsAction = { label: 'Find voice plugins', href: '#/plugins?kind=voice' };
  } else {
    ttsDetail = 'Pick the voice the puck speaks with.';
  }
  steps.push({ id: 'tts', title: 'Pick a voice', done: !!tts?.ready || textOnly, detail: ttsDetail, action: ttsAction, extra: ttsExtra });

  // 4. Household
  const loc = String(house.location || '').trim();
  const tz = String(house.timeZone || '').trim();
  steps.push({
    id: 'home',
    title: 'Tell it about your home',
    done: !!(loc && tz),
    detail: loc && tz ? `${loc} · ${tz}` : loc ? `${loc}. Add your time zone too (one click on the Household page).` : 'Where you live (for weather and local answers) and your time zone.',
    action: { label: loc && tz ? 'Household settings' : 'Add location', href: '#/household' },
  });

  // 5. A puck
  const known = s.pucks || [];
  const connected = known.filter((p) => p.connected);
  let puckDetail;
  if (connected.length) {
    puckDetail = connected.length === 1
      ? `${connected[0].name} is connected over ${(connected[0].transports || []).map((t) => (t === 'usb' ? 'USB' : 'Wi-Fi')).join(' and ') || 'the network'}.`
      : `${plural(connected.length, 'puck')} connected: ${connected.map((p) => p.name).join(', ')}.`;
  } else if (known.length) {
    puckDetail = `${known[0].name} is offline (last seen ${fmtAgo(known[0].lastSeen)}). Is it switched on and in Wi-Fi range?`;
  } else if ((s.pending || []).length) {
    puckDetail = 'A puck is waiting for you to approve it (see above).';
  } else {
    puckDetail = 'Flash it, put it on your Wi-Fi, then approve it here.';
  }
  const usbNote = !connected.length && usbPorts?.length
    ? h('p', { class: 'step-note' }, icon('usb', { size: 15 }), `Something that looks like a puck is plugged in by USB (${usbPorts.join(', ')}). If it’s a puck, it should appear in a moment.`)
    : null;
  steps.push({
    id: 'puck',
    title: 'Connect a puck',
    done: connected.length > 0,
    warn: !connected.length && known.length > 0,
    detail: puckDetail,
    action: { label: known.length ? 'Pucks' : 'Pucks page', href: '#/pucks' },
    more: connected.length ? null : h('div', null, usbNote, howToConnect({ compact: true })),
  });

  // 6. Plugins
  const plugins = (s.plugins || []).filter((p) => !isSpeechOnly(p));
  const on = plugins.filter((p) => p.enabled);
  const working = on.filter((p) => levelClass(p.status?.level) === 'ok');
  const attention = on.filter((p) => ['warn', 'err'].includes(levelClass(p.status?.level)));
  const off = plugins.filter((p) => !p.enabled);
  const show = [...attention, ...working].slice(0, 6);
  const list = show.length
    ? h('ul', { class: 'step-plugins' }, show.map((p) => h('li', null,
      h('a', { href: `#/plugins/${encodeURIComponent(p.id)}` }, statusDot(p.status?.level), h('strong', null, p.name)),
      h('span', { class: 'muted' }, p.status?.message || ''))))
    : null;
  steps.push({
    id: 'plugins',
    title: 'Try some plugins',
    done: working.length > 0,
    detail: on.length
      ? `${plural(on.length, 'plugin')} on${attention.length ? `, ${attention.length} need${attention.length === 1 ? 's' : ''} a look` : ''}.${off.length ? ` Not on yet: ${off.slice(0, 4).map((p) => p.name).join(', ')}${off.length > 4 ? '…' : ''}.` : ''}`
      : `Plugins let Claude play music, run the heating, control the TV and more.${off.length ? ` Try ${off.slice(0, 3).map((p) => p.name).join(', ')}.` : ''}`,
    action: { label: 'Browse plugins', href: '#/plugins' },
    more: list,
    alwaysMore: true,
  });
  return steps;
}

function checklistCard(steps, { onHide } = {}) {
  const done = steps.filter((x) => x.done).length;
  const nextIdx = steps.findIndex((x) => !x.done);
  const card = h('section', { class: 'card checklist', 'aria-labelledby': 'checklist-title' });
  const allDone = done === steps.length;
  card.append(h('div', { class: 'checklist-head' },
    h('div', null,
      h('h2', { id: 'checklist-title' }, allDone ? 'Everything’s set up' : 'Get set up'),
      h('p', { class: 'muted' }, allDone ? 'Claude can hear, think and speak. You can come back here any time.' : `${done} of ${steps.length} done. Each step links to where you do it.`)),
    h('div', { class: 'progress-ring', style: { '--p': `${(done / steps.length) * 100}` }, role: 'img', 'aria-label': `${done} of ${steps.length} steps done` }, h('span', null, `${done}/${steps.length}`))));
  const ol = h('ol', { class: 'checklist-steps' });
  steps.forEach((st, i) => {
    const isNext = i === nextIdx;
    const mark = st.done
      ? h('span', { class: 'step-mark is-done', 'aria-hidden': 'true' }, icon('check', { size: 15, stroke: 2.4 }))
      : h('span', { class: ['step-mark', st.warn && 'is-warn', isNext && 'is-next'], 'aria-hidden': 'true' }, String(i + 1));
    const showMore = st.more && (isNext || st.alwaysMore);
    ol.append(h('li', { class: ['step', st.done && 'is-done', isNext && 'is-next'] },
      mark,
      h('div', { class: 'step-body' },
        h('div', { class: 'step-title' }, st.title, h('span', { class: 'sr-only' }, st.done ? ' (done)' : ' (to do)')),
        h('div', { class: 'step-detail' }, st.detail),
        showMore ? h('div', { class: 'step-more' }, st.more) : null,
        h('div', { class: 'step-actions' },
          h('a', { class: ['btn', 'btn-sm', isNext ? 'btn-primary' : ''], href: st.action.href }, st.action.label, icon('arrow-right', { size: 15 })),
          st.extra))));
  });
  card.append(ol);
  if (allDone && onHide) card.append(h('div', { class: 'checklist-foot' }, h('button', { type: 'button', class: 'linkish', onClick: onHide }, 'Hide this checklist')));
  return card;
}

export default {
  title: 'Home',
  mount(root) {
    const head = h('div', { class: 'page-head' });
    const pendingHost = h('div');
    const checklistHost = h('div', { class: 'home-checklist' });
    const pucksBody = h('div', { class: 'puck-tiles' });
    const phoneHost = h('div');

    // Quick question
    const askInput = h('input', { class: 'input', type: 'text', placeholder: 'What’s the weather like tomorrow?', 'aria-label': 'Ask Claude something', autocomplete: 'off' });
    const askBtn = h('button', { type: 'submit', class: 'btn btn-primary' }, icon('send', { size: 16 }), 'Ask');
    const askOut = h('div', { class: 'ask-out', 'aria-live': 'polite' });
    const askForm = h('form', {
      class: 'ask-row',
      onSubmit: async (e) => {
        e.preventDefault();
        const text = askInput.value.trim();
        if (!text) return askInput.focus();
        askBtn.disabled = true;
        askOut.replaceChildren(h('div', { class: 'ask-q' }, text), h('div', { class: 'ask-thinking' }, spinner(), 'Claude is thinking…'));
        try {
          const r = await post('/api/chat', { text, session: 'web' });
          askOut.replaceChildren(
            h('div', { class: 'ask-q' }, text),
            h('div', { class: 'ask-acts' }, activityRows(r.activities)),
            h('div', { class: ['ask-a', 'md', r.failed && 'is-error'] }, markdown(r.text || '(no answer)')),
            h('a', { class: 'linkish ask-more', href: '#/talk' }, 'Continue in Talk', icon('arrow-right', { size: 14 })));
          askInput.value = '';
        } catch (err) {
          askOut.replaceChildren(h('div', { class: 'ask-a is-error' }, err.message));
        } finally {
          askBtn.disabled = false;
        }
      },
    }, askInput, askBtn);

    const grid = h('div', { class: 'home-grid' },
      checklistHost,
      h('div', { class: 'home-side' },
        h('section', { class: 'card' },
          h('div', { class: 'card-head' }, h('h2', null, 'Pucks'), h('a', { class: 'linkish', href: '#/pucks' }, 'Manage', icon('arrow-right', { size: 14 }))),
          pucksBody),
        h('section', { class: 'card' },
          h('div', { class: 'card-head' }, h('h2', null, 'Ask something')),
          askForm, askOut),
        phoneHost));
    root.append(head, pendingHost, grid);

    let usbPorts = [];
    let lastSig = '';
    let lastPuckSig = '';
    let lastPendingSig = '';
    let usbTimer = null;

    const render = () => {
      const s = store.status;
      if (!s) return;
      const steps = buildSteps(s, usbPorts);
      const allDone = steps.every((x) => x.done);
      // Header
      const sig = JSON.stringify([steps.map((x) => [x.done, x.warn, detailText(x.detail), x.action.label]), (s.plugins || []).map((p) => [p.id, p.enabled, p.status?.level, p.status?.message]), usbPorts, storage.get('puck.checklistHidden', false)]);
      head.replaceChildren(h('div', null,
        h('h1', null, allDone ? `${s.hub?.name || 'Your hub'} is ready` : 'Let’s set up your hub'),
        h('p', { class: 'lede' }, allDone
          ? 'Talk to a puck, or ask something here. Everything else lives in the menu.'
          : 'A few steps and Claude will be listening through your puck. Here’s what’s left.')));
      // Pending approvals (only redrawn when they change, so focus isn't lost)
      const pendSig = JSON.stringify((s.pending || []).map((p) => [p.id, p.code, p.name, p.address]));
      if (pendSig !== lastPendingSig) {
        lastPendingSig = pendSig;
        pendingHost.replaceChildren(pendingBanner(s.pending));
      }
      if (sig !== lastSig) {
        lastSig = sig;
        const hidden = allDone && storage.get('puck.checklistHidden', false);
        checklistHost.replaceChildren(hidden
          ? h('section', { class: 'card checklist-done' }, icon('check-circle', { size: 20 }), h('span', null, 'Setup complete.'), h('button', { type: 'button', class: 'linkish', onClick: () => { storage.set('puck.checklistHidden', false); lastSig = ''; render(); } }, 'Show the checklist'))
          : checklistCard(steps, { onHide: () => { storage.set('puck.checklistHidden', true); lastSig = ''; render(); } }));
      }
      // Pucks
      const psig = puckSignature(s);
      if (psig !== lastPuckSig) {
        lastPuckSig = psig;
        const list = [...(s.pucks || [])].sort((x, y) => Number(y.connected) - Number(x.connected));
        pucksBody.replaceChildren(...(list.length
          ? list.map((p) => puckTile(p))
          : [h('p', { class: 'muted' }, 'No pucks yet. The checklist shows how to connect one.')]));
      }
      // Phone link (only shown on the hub's own computer)
      const adminUrl = s.hub?.adminUrl;
      if (adminUrl && !phoneHost.dataset.url) {
        phoneHost.dataset.url = adminUrl;
        phoneHost.replaceChildren(h('section', { class: 'card' },
          h('div', { class: 'card-head' }, h('h2', null, 'Set up from your phone')),
          h('p', { class: 'muted small' }, 'Open this link on a phone or tablet on the same Wi-Fi. It contains the hub’s key, so keep it to yourself.'),
          h('div', { class: 'url-box' }, h('code', { class: 'url-code' }, adminUrl), copyButton(adminUrl))));
      }
      // Keep an eye on USB while no puck is connected
      const needUsb = !(s.pucks || []).some((p) => p.connected);
      if (needUsb && !usbTimer) {
        const tick = async () => {
          try {
            const ports = await get('/api/usb-ports', { timeout: 8000 });
            const next = Array.isArray(ports) ? ports : [];
            if (JSON.stringify(next) !== JSON.stringify(usbPorts)) {
              usbPorts = next;
              render();
            }
          } catch { /* ignore */ }
        };
        tick();
        usbTimer = setInterval(tick, 6000);
      } else if (!needUsb && usbTimer) {
        clearInterval(usbTimer);
        usbTimer = null;
        usbPorts = [];
      }
    };

    // The checklist needs the voice and household settings too
    Promise.all([loadSection('voice', true), loadSection('household', true)]).then(() => { lastSig = ''; render(); });
    const offSection = on('section', () => { lastSig = ''; render(); });

    render();
    return {
      update: render,
      unmount() {
        offSection();
        if (usbTimer) clearInterval(usbTimer);
      },
    };
  },
};

function detailText(d) {
  if (d == null) return '';
  if (typeof d === 'string') return d;
  if (Array.isArray(d)) return d.map(detailText).join('');
  return d.textContent || '';
}

function puckSignature(s) {
  return JSON.stringify([(s.pucks || []).map((p) => [p.id, p.name, p.room, p.connected, p.voice, p.rssi, p.battery, p.charging, p.lastSeen && !p.connected ? p.lastSeen : '']), (s.pending || []).length]);
}
