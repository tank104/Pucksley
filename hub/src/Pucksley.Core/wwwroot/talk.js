// Talk: chat with the assistant by typing or holding the mic; the live conversation (pucks included).

import { h, toast, storage, spinner } from './dom.js';
import { icon } from './icons.js';
import { post, request } from './api.js';
import { store, on, connectedPucks } from './store.js';
import { createChatView } from './components.js';
import { selectWrap } from './forms.js';
import { micAvailability, startRecording, playBlob, stopPlayback, unlockAudio, base64ToBlob } from './audio.js';

const HOLD_MS = 350;

export default {
  title: 'Talk',
  mount(root) {
    let alive = true;
    const offs = [];

    // ---------------------------------------------------------------- transcript
    const scroll = h('div', { class: 'talk-scroll' });
    const chat = createChatView({
      scroller: scroll,
      emptyText: 'Say hello: type below, or hold the microphone and talk. Conversations with your pucks show up here too.',
    });
    const thinking = h('div', { class: 'chat-thinking', hidden: true, 'aria-live': 'polite' },
      h('span', { class: 'avatar', 'aria-hidden': 'true' }, icon('sparkle', { size: 15 })),
      h('span', { class: 'typing', 'aria-hidden': 'true' }, h('i'), h('i'), h('i')),
      h('span', { class: 'thinking-text' }, 'Thinking…'));
    const jump = h('button', { type: 'button', class: 'jump-latest', hidden: true, onClick: () => chat.scrollToEnd() }, icon('arrow-down', { size: 15 }), 'Latest');
    scroll.append(chat.el, thinking);
    scroll.addEventListener('scroll', () => { jump.hidden = chat.nearBottom(); });

    let filterMode = storage.get('puck.talkFilter', 'all');
    const applyFilter = () => chat.setFilter(filterMode === 'web' ? (ev) => String(ev.session || '').startsWith('web:') : null);
    const seg = h('div', { class: 'seg', role: 'group', 'aria-label': 'Show' });
    const segBtn = (mode, label) => {
      const b = h('button', { type: 'button', class: 'seg-btn', 'aria-pressed': String(filterMode === mode), onClick: () => {
        filterMode = mode;
        storage.set('puck.talkFilter', mode);
        seg.querySelectorAll('.seg-btn').forEach((x) => x.setAttribute('aria-pressed', String(x === b)));
        applyFilter();
      } }, label);
      return b;
    };
    seg.append(segBtn('all', 'Everything'), segBtn('web', 'This page only'));

    const newBtn = h('button', { type: 'button', class: 'btn btn-sm', onClick: async () => {
      try {
        await Promise.all([post('/api/chat/reset', { session: 'web' }), post('/api/chat/reset', { session: 'voice' })]);
        chat.divider('New conversation: Claude has forgotten the earlier messages');
        toast('Started a new conversation.', 'ok', 2500);
      } catch (e) {
        toast(e.message, 'error');
      }
    }, title: 'New conversation', 'aria-label': 'New conversation' }, icon('new-chat', { size: 16 }), h('span', { class: 'btn-label' }, 'New conversation'));

    const head = h('div', { class: 'talk-head' },
      h('div', null, h('h1', null, 'Talk'), h('p', { class: 'lede small' }, 'Type or hold the mic. What your pucks hear and say appears here as it happens.')),
      h('div', { class: 'talk-tools' }, seg, newBtn));

    // ---------------------------------------------------------------- composer
    const input = h('textarea', { class: 'composer-input', rows: 1, placeholder: 'Message Claude…', 'aria-label': 'Message Claude', enterkeyhint: 'send' });
    const sendBtn = h('button', { type: 'submit', class: 'composer-send', 'aria-label': 'Send', title: 'Send (Enter)' }, icon('send', { size: 19, stroke: 2.2 }));
    const mic = h('button', { type: 'button', class: 'mic-btn', 'aria-label': 'Hold to talk', title: 'Hold to talk, or tap to start and tap again to send' }, icon('mic', { size: 20 }));
    const micStatus = h('div', { class: 'mic-status', hidden: true, 'aria-live': 'assertive' });
    const speakSel = h('select', { class: 'input select select-sm', 'aria-label': 'Speak replies' });
    const micNote = h('div', { class: 'composer-note', hidden: true });

    const form = h('form', { class: 'composer', onSubmit: (e) => { e.preventDefault(); sendText(); } },
      micStatus,
      h('div', { class: 'composer-box' }, input, h('div', { class: 'composer-actions' }, mic, sendBtn)),
      h('div', { class: 'composer-opts' },
        h('label', { class: 'composer-opt' }, icon('speaker', { size: 15 }), h('span', null, 'Speak replies'), selectWrap(speakSel)),
        micNote));

    root.append(h('div', { class: 'talk' }, head, h('div', { class: 'talk-body' }, scroll, jump), form));

    const autosize = () => {
      input.style.height = 'auto';
      input.style.height = `${Math.min(input.scrollHeight, 180)}px`;
    };
    input.addEventListener('input', autosize);
    input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
        e.preventDefault();
        sendText();
      }
    });

    // ---------------------------------------------------------------- "speak replies" choices
    let speakChoice = storage.get('puck.speakOn', '');
    let speakSig = null;
    function renderSpeak() {
      const sig = JSON.stringify(connectedPucks().map((p) => [p.id, p.name]));
      if (sig === speakSig) return;
      speakSig = sig;
      const opts = [{ value: '', label: 'Nowhere (text only)' }, { value: 'browser', label: 'In this browser' }];
      for (const p of connectedPucks()) opts.push({ value: `puck:${p.id}`, label: `On ${p.name}` });
      if (speakChoice.startsWith('puck:') && !opts.some((o) => o.value === speakChoice)) opts.push({ value: speakChoice, label: 'On a puck (offline)', disabled: true });
      speakSel.replaceChildren(...opts.map((o) => h('option', { value: o.value, disabled: o.disabled || null }, o.label)));
      speakSel.value = speakChoice;
    }
    speakSel.addEventListener('change', () => {
      speakChoice = speakSel.value;
      storage.set('puck.speakOn', speakChoice);
      if (speakChoice === 'browser') unlockAudio();
    });
    renderSpeak();

    // ---------------------------------------------------------------- the conversation feed
    const pending = []; // turns waiting for the hub's answer: {session, done}
    function updateThinking() {
      const busy = pending.some((t) => !t.done) || micState === 'sending';
      thinking.hidden = !busy;
      if (busy) requestAnimationFrame(() => { if (chat.nearBottom()) chat.scrollToEnd(); });
    }
    chat.reset(store.conversation);
    applyFilter();
    offs.push(on('conversation', (ev) => {
      chat.add(ev);
      if ((ev.role === 'assistant' || ev.role === 'error') && String(ev.session).startsWith('web:')) {
        const t = pending.find((x) => !x.done && x.session === ev.session);
        if (t) {
          t.done = true;
          t.sawReply = true;
        }
        updateThinking();
      }
      if (!chat.nearBottom()) jump.hidden = false;
    }));
    offs.push(on('conversation-reset', (list) => chat.reset(list)));

    // ---------------------------------------------------------------- typing
    async function speakInBrowser(text) {
      try {
        const wav = await post('/api/voice/speak', { text });
        await playBlob(wav);
      } catch (e) {
        toast(`Couldn’t speak the reply: ${e.message}`, 'error');
      }
    }

    async function sendText() {
      const text = input.value.trim();
      if (!text) return input.focus();
      input.value = '';
      autosize();
      if (speakChoice === 'browser') unlockAudio();
      const at = new Date().toISOString();
      chat.add({ session: 'web:web', role: 'user', text, at }, { local: true });
      requestAnimationFrame(() => chat.scrollToEnd());
      const turn = { session: 'web:web', done: false, sawReply: false };
      pending.push(turn);
      updateThinking();
      const body = { text, session: 'web' };
      if (speakChoice.startsWith('puck:')) body.speakOn = speakChoice.slice(5);
      try {
        const r = await post('/api/chat', body);
        // The live feed normally shows the answer; if it didn't (or isn't connected), show it now.
        const fallback = () => {
          if (!turn.sawReply) chat.add({ session: 'web:web', role: r.failed ? 'error' : 'assistant', text: r.text || '(no answer)', at: new Date().toISOString() }, { local: true });
          turn.done = true;
          updateThinking();
        };
        if (store.live && !turn.sawReply) setTimeout(fallback, 1200);
        else fallback();
        if (!r.failed && speakChoice === 'browser' && r.text) speakInBrowser(r.text);
      } catch (e) {
        turn.done = true;
        updateThinking();
        chat.add({ session: 'web:web', role: 'error', text: e.message, at: new Date().toISOString() }, { local: true });
      }
    }

    // ---------------------------------------------------------------- the microphone
    const avail = micAvailability();
    if (!avail.ok) {
      mic.classList.add('is-unavailable');
      mic.setAttribute('aria-label', 'Voice input unavailable');
      micNote.hidden = false;
      micNote.replaceChildren(icon('info', { size: 15 }), h('span', null, avail.message));
    }

    let micState = 'idle'; // idle | starting | recording | sending
    let rec = null;
    let pressAt = 0;
    let holding = false; // the button is still held down
    let toggleMode = false;
    let tick = null;

    function setMic(state, text) {
      micState = state;
      mic.dataset.state = state;
      // Not `disabled`: a disabled button can miss the pointerup of a quick tap
      mic.setAttribute('aria-disabled', String(state === 'sending' || state === 'starting'));
      micStatus.hidden = state === 'idle';
      if (state === 'recording') {
        const cancel = h('button', { type: 'button', class: 'icon-btn', 'aria-label': 'Cancel recording', onClick: () => cancelRecording() }, icon('x', { size: 16 }));
        const time = h('span', { class: 'mic-time' }, '0:00');
        micStatus.replaceChildren(h('span', { class: 'rec-dot', 'aria-hidden': 'true' }), h('span', { class: 'mic-text' }, text), time, cancel);
        clearInterval(tick);
        tick = setInterval(() => {
          const s = Math.floor(rec?.seconds || 0);
          time.textContent = `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
        }, 250);
      } else {
        clearInterval(tick);
        if (state === 'idle') mic.style.removeProperty('--level');
        micStatus.replaceChildren(spinner('spinner-sm'), h('span', { class: 'mic-text' }, text || ''));
      }
      updateThinking();
    }

    async function beginRecording() {
      if (micState !== 'idle') return;
      if (!avail.ok) {
        toast(avail.message, 'info', 7000);
        return;
      }
      unlockAudio();
      stopPlayback();
      setMic('starting', 'Starting the microphone…');
      try {
        rec = await startRecording({
          onLevel: (l) => mic.style.setProperty('--level', l.toFixed(3)),
          onLimit: () => finishRecording(),
          maxSeconds: 45,
        });
      } catch (e) {
        rec = null;
        setMic('idle');
        const denied = e && (e.name === 'NotAllowedError' || e.name === 'SecurityError');
        toast(denied ? 'The browser wasn’t allowed to use the microphone. Allow it in the address bar’s site settings and try again.' : e?.name === 'NotFoundError' ? 'No microphone found.' : `Couldn’t start the microphone: ${e?.message || e}`, 'error', 8000);
        return;
      }
      if (!alive) { rec.cancel(); return; }
      // Released while the permission prompt was up: treat as a tap (keep recording until tapped again)
      if (!holding && !toggleMode) toggleMode = true;
      setMic('recording', toggleMode ? 'Listening… tap the mic again to send' : 'Listening… let go to send');
    }

    async function cancelRecording() {
      const r = rec;
      rec = null;
      toggleMode = false;
      holding = false;
      setMic('idle');
      await r?.cancel();
    }

    async function finishRecording() {
      if (micState !== 'recording' || !rec) return;
      const r = rec;
      rec = null;
      toggleMode = false;
      holding = false;
      setMic('sending', 'Sending your recording…');
      let result;
      try {
        result = await r.stop();
      } catch (e) {
        setMic('idle');
        toast(`Recording failed: ${e.message}`, 'error');
        return;
      }
      if (!result || result.seconds < 0.4) {
        setMic('idle');
        toast('That was very short. Hold the mic while you talk, or tap once to start and again to send.', 'info', 5000);
        return;
      }
      if (result.peak < 0.004) toast('The recording is almost silent. Is the right microphone selected?', 'info', 6000);
      setMic('sending', 'Listening to what you said…');
      const turn = { session: 'web:voice', done: false, sawReply: false };
      pending.push(turn);
      try {
        const r2 = await request('POST', '/api/voice/ask', { body: result.wav, contentType: 'audio/wav' });
        turn.done = true;
        if (r2.heard) {
          chat.add({ session: 'web:voice', role: 'user', text: r2.heard, at: new Date().toISOString() }, { local: true });
          chat.add({ session: 'web:voice', role: r2.failed ? 'error' : 'assistant', text: r2.reply || '', at: new Date().toISOString() }, { local: true });
        } else {
          toast(r2.reply || 'Sorry, I didn’t catch that.', 'info', 5000);
        }
        setMic('idle');
        requestAnimationFrame(() => chat.scrollToEnd());
        if (r2.audio) {
          try {
            await playBlob(base64ToBlob(r2.audio));
          } catch {
            toast('Couldn’t play the reply here. Tap the page and try again.', 'info');
          }
        } else if (r2.heard && !r2.failed) {
          toast('No voice is set up, so the reply is text only.', 'info', 4000);
        }
      } catch (e) {
        turn.done = true;
        setMic('idle');
        chat.add({ session: 'web:voice', role: 'error', text: e.message, at: new Date().toISOString() }, { local: true });
      }
      updateThinking();
    }

    function pressStart() {
      if (micState === 'sending') return;
      pressAt = performance.now();
      holding = true;
      if (micState === 'recording') {
        // A tap while recording (toggle mode) sends it
        finishRecording();
        return;
      }
      if (micState === 'idle') {
        toggleMode = false;
        beginRecording();
      }
    }

    function pressEnd() {
      if (!holding) return;
      holding = false;
      const held = performance.now() - pressAt;
      if (micState === 'recording' && !toggleMode) {
        if (held < HOLD_MS) {
          toggleMode = true;
          setMic('recording', 'Listening… tap the mic again to send');
        } else {
          finishRecording();
        }
      } else if (micState === 'starting' && held < HOLD_MS) {
        toggleMode = true;
      }
    }

    mic.addEventListener('pointerdown', (e) => {
      if (e.button !== 0) return;
      e.preventDefault();
      try { mic.setPointerCapture(e.pointerId); } catch { /* ignore */ }
      pressStart();
    });
    mic.addEventListener('pointerup', () => pressEnd());
    mic.addEventListener('pointercancel', () => pressEnd());
    mic.addEventListener('contextmenu', (e) => e.preventDefault());
    mic.addEventListener('keydown', (e) => {
      if ((e.key === ' ' || e.key === 'Enter') && !e.repeat) {
        e.preventDefault();
        pressStart();
      } else if (e.key === 'Escape' && micState === 'recording') {
        cancelRecording();
      }
    });
    mic.addEventListener('keyup', (e) => {
      if (e.key === ' ' || e.key === 'Enter') {
        e.preventDefault();
        pressEnd();
      }
    });
    const onEsc = (e) => { if (e.key === 'Escape' && micState === 'recording') cancelRecording(); };
    document.addEventListener('keydown', onEsc);

    setMic('idle');
    requestAnimationFrame(() => chat.scrollToEnd());
    if (window.matchMedia('(pointer: fine)').matches) setTimeout(() => input.focus({ preventScroll: true }), 50);

    return {
      update() {
        renderSpeak();
      },
      unmount() {
        alive = false;
        offs.forEach((f) => f());
        document.removeEventListener('keydown', onEsc);
        clearInterval(tick);
        if (rec) rec.cancel();
      },
    };
  },
};
