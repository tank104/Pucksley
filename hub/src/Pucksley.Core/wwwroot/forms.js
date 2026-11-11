// Schema-driven settings forms (the hub's SettingsSchema): string text int number bool select pictures lines list,
// secrets (masked), advanced fields, help, save states, unsaved-changes tracking.

import { h, clear, uid, linkify, capitalize, spinner } from './dom.js';
import { icon } from './icons.js';

/** What the hub shows (and wants back) for a secret that has a value: 8 × U+2022. */
export const MASK = '•'.repeat(8);

const live = new Set();

/** True when a form on the page has unsaved changes. */
export function anyDirty() {
  for (const f of live) if (f.el.isConnected && f.isDirty()) return true;
  return false;
}

/** Throw away every unsaved change (the person chose to leave). */
export function discardAll() {
  for (const f of live) if (f.isDirty()) f.revert();
}

/**
 * createForm({ schema, values, onSave, custom, saveLabel, onChange, emptyText, extraActions, pictureBase })
 * pictureBase: where 'pictures' fields load their pictures from (a plugin's '/plugins/{id}/').
 * onSave(values) → {schema, values} | undefined  (throw to show an error)
 * custom: { key: (def, value, ctx) => control } replaces a field's input.
 * A control is { id, el, get(), set(v), validate?(), focus?(), destroy?(), describe?(ids), invalid?(on) }.
 */
export function createForm(opts) {
  const { onSave, custom = {}, saveLabel = 'Save', onChange, emptyText = 'There’s nothing to set up here.', extraActions = null } = opts;
  const prefix = uid('f');
  let fields = [];
  let schemaKey = '';
  let lastData = { schema: null, values: null };
  let initial = '';
  let state = 'idle';
  let savedTimer = null;

  const mainBody = h('div', { class: 'sform-fields' });
  const advBody = h('div', { class: 'sform-fields' });
  const advanced = h('details', { class: 'sform-advanced' },
    h('summary', null, icon('chevron-right', { size: 16, cls: 'chev' }), h('span', null, 'Advanced')),
    advBody);
  const statusEl = h('span', { class: 'sform-status', role: 'status', 'aria-live': 'polite' });
  const discardBtn = h('button', { type: 'button', class: 'btn btn-ghost', onClick: () => revert() }, 'Discard');
  const saveBtn = h('button', { type: 'submit', class: 'btn btn-primary' }, saveLabel);
  const bar = h('div', { class: 'sform-bar' }, statusEl, h('div', { class: 'sform-bar-actions' }, extraActions, discardBtn, saveBtn));
  const emptyEl = h('p', { class: 'muted sform-empty' }, emptyText);
  const el = h('form', {
    class: 'sform',
    novalidate: true,
    autocomplete: 'off',
    onSubmit: (e) => { e.preventDefault(); save(); },
    onKeyDown: (e) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
        e.preventDefault();
        save();
      }
    },
  }, emptyEl, mainBody, advanced, bar);

  const ctx = {
    id: (key) => `${prefix}-${key}`,
    changed: () => {
      if (state === 'error' || state === 'saved') setState('idle');
      // A field marked wrong clears as soon as it's fixed
      for (const f of fields) {
        if (f.wrap.classList.contains('has-error') && !(f.control.validate?.())) {
          f.err.hidden = true;
          f.wrap.classList.remove('has-error');
          f.control.invalid?.(false);
        }
      }
      updateDirty();
      onChange?.(api);
    },
    custom,
    pictureBase: opts.pictureBase || '',
  };

  function build(schema, values) {
    for (const f of fields) f.control.destroy?.();
    clear(mainBody);
    clear(advBody);
    fields = [];
    for (const def of schema?.fields || []) {
      const control = makeControl(def, values ? values[def.key] : undefined, ctx);
      const { wrap, err } = fieldWrap(def, control);
      (def.advanced ? advBody : mainBody).append(wrap);
      fields.push({ def, control, wrap, err });
    }
    schemaKey = JSON.stringify(schema || {});
    advanced.hidden = !fields.some((f) => f.def.advanced);
    mainBody.hidden = !fields.some((f) => !f.def.advanced);
    emptyEl.hidden = fields.length > 0;
    bar.hidden = fields.length === 0;
  }

  function snapshot() {
    return JSON.stringify(getValues());
  }

  function getValues() {
    const v = {};
    for (const f of fields) v[f.def.key] = f.control.get();
    return v;
  }

  function isDirty() {
    return fields.length > 0 && snapshot() !== initial;
  }

  function updateDirty() {
    const dirty = isDirty();
    el.classList.toggle('is-dirty', dirty);
    saveBtn.disabled = state === 'saving' || !dirty;
    discardBtn.hidden = !dirty || state === 'saving';
    if (state === 'idle') {
      statusEl.replaceChildren(dirty ? h('span', { class: 'sform-dirty' }, h('span', { class: 'dot warn' }), 'Unsaved changes') : '');
    }
  }

  function setState(s, message) {
    state = s;
    clearTimeout(savedTimer);
    if (s === 'saving') statusEl.replaceChildren(spinner(), h('span', null, 'Saving…'));
    else if (s === 'saved') {
      statusEl.replaceChildren(h('span', { class: 'sform-saved' }, icon('check', { size: 16 }), 'Saved'));
      savedTimer = setTimeout(() => { if (state === 'saved') { state = 'idle'; updateDirty(); } }, 2600);
    } else if (s === 'error') statusEl.replaceChildren(h('span', { class: 'sform-error' }, icon('alert', { size: 16 }), message || 'Couldn’t save.'));
    updateDirty();
  }

  function showErrors() {
    let first = null;
    for (const f of fields) {
      const msg = f.control.validate?.() || null;
      f.err.textContent = msg || '';
      f.err.hidden = !msg;
      f.control.invalid?.(!!msg);
      f.wrap.classList.toggle('has-error', !!msg);
      if (msg && !first) first = f;
    }
    return first;
  }

  async function save() {
    if (state === 'saving' || !fields.length) return false;
    const bad = showErrors();
    if (bad) {
      if (bad.def.advanced) advanced.open = true;
      bad.control.focus?.();
      setState('error', 'Please check the highlighted field.');
      return false;
    }
    setState('saving');
    try {
      const result = await onSave(getValues(), api);
      if (result && result.schema) setData(result.schema, result.values);
      else initial = snapshot();
      setState('saved');
      return true;
    } catch (e) {
      setState('error', e?.message ? `Couldn’t save: ${e.message}` : 'Couldn’t save.');
      return false;
    }
  }

  function setData(schema, values) {
    lastData = { schema, values };
    const key = JSON.stringify(schema || {});
    if (key === schemaKey && fields.length) {
      for (const f of fields) {
        f.control.set(values ? values[f.def.key] : undefined);
        f.err.hidden = true;
        f.wrap.classList.remove('has-error');
      }
    } else {
      build(schema, values);
    }
    initial = snapshot();
    updateDirty();
  }

  function revert() {
    setData(lastData.schema, lastData.values);
    setState('idle');
    onChange?.(api);
  }

  function destroy() {
    for (const f of fields) f.control.destroy?.();
    live.delete(api);
  }

  const api = {
    el,
    isDirty,
    getValues,
    save,
    revert,
    setData,
    destroy,
    field: (key) => fields.find((f) => f.def.key === key)?.control || null,
    get saving() { return state === 'saving'; },
  };

  setData(opts.schema, opts.values);
  live.add(api);
  return api;
}

// ------------------------------------------------------------------ fields

function fieldWrap(def, control) {
  const id = control.id;
  const help = def.help ? h('p', { class: 'field-help', id: `${id}-help` }, linkify(def.help)) : null;
  const err = h('p', { class: 'field-error', id: `${id}-err`, hidden: true });
  control.describe?.([help ? `${id}-help` : null, `${id}-err`].filter(Boolean).join(' '));
  let wrap;
  const type = def.type || 'string';
  if (type === 'bool' && !control.custom) {
    wrap = h('div', { class: 'field field-bool' },
      h('div', { class: 'field-text' }, h('label', { class: 'field-label', for: id }, def.label), help, err),
      control.el);
  } else if (type === 'list' || (type === 'pictures' && !control.custom)) {
    wrap = h('fieldset', { class: `field field-${type}` }, h('legend', { class: 'field-label' }, def.label), help, control.el, err);
  } else {
    wrap = h('div', { class: `field field-${type}` }, h('label', { class: 'field-label', for: id }, def.label), control.el, help, err);
  }
  return { wrap, err };
}

export function makeControl(def, value, ctx) {
  const c = ctx.custom?.[def.key]?.(def, value, ctx);
  if (c) {
    c.custom = true;
    return c;
  }
  const type = def.type || 'string';
  if (def.secret && (type === 'string' || type === 'text')) return secretControl(def, value, ctx);
  switch (type) {
    case 'bool': return boolControl(def, value, ctx);
    case 'int':
    case 'number': return numberControl(def, value, ctx);
    case 'select': return selectControl(def, value, ctx);
    case 'pictures': return picturesControl(def, value, ctx);
    case 'text': return textControl(def, value, ctx, true);
    case 'lines': return linesControl(def, value, ctx);
    case 'list': return listControl(def, value, ctx);
    default: return textControl(def, value, ctx, false);
  }
}

function common(input) {
  return {
    focus: () => input.focus(),
    describe: (ids) => input.setAttribute('aria-describedby', ids),
    invalid: (on) => input.setAttribute('aria-invalid', on ? 'true' : 'false'),
  };
}

function str(v) {
  if (v == null) return '';
  if (typeof v === 'object') return JSON.stringify(v);
  return String(v);
}

export function textControl(def, value, ctx, multiline = false) {
  const id = ctx.id(def.key);
  const input = multiline
    ? h('textarea', { id, class: 'input', rows: 4, placeholder: def.placeholder || null, onInput: ctx.changed })
    : h('input', { id, class: 'input', type: 'text', placeholder: def.placeholder || null, onInput: ctx.changed, autocomplete: 'off', spellcheck: 'false' });
  const set = (v) => {
    input.value = str(v);
    if (multiline) input.rows = Math.min(14, Math.max(4, input.value.split('\n').length + 1));
  };
  set(value);
  return { id, el: input, input, get: () => input.value, set, ...common(input) };
}

function numberControl(def, value, ctx) {
  const id = ctx.id(def.key);
  const isInt = def.type === 'int';
  const input = h('input', { id, class: 'input input-num', type: 'text', inputmode: isInt ? 'numeric' : 'decimal', placeholder: def.placeholder || null, onInput: ctx.changed, autocomplete: 'off' });
  let wasNull = value == null;
  const norm = () => input.value.trim().replace(',', '.');
  const set = (v) => {
    wasNull = v == null;
    input.value = v == null ? '' : String(v);
  };
  set(value);
  return {
    id,
    el: input,
    get() {
      const s = norm();
      if (s === '') return null;
      const n = Number(s);
      return Number.isFinite(n) ? n : s;
    },
    set,
    validate() {
      const s = norm();
      if (s === '') return wasNull || def.nullable ? null : 'Enter a number.';
      const n = Number(s);
      if (!Number.isFinite(n)) return 'That isn’t a number.';
      if (isInt && !Number.isInteger(n)) return 'Enter a whole number.';
      if (def.min != null && n < def.min) return `The smallest allowed is ${def.min}.`;
      if (def.max != null && n > def.max) return `The largest allowed is ${def.max}.`;
      return null;
    },
    ...common(input),
  };
}

export function selectWrap(sel) {
  return h('div', { class: 'select-wrap' }, sel, icon('chevron-down', { size: 16, cls: 'select-chev' }));
}

function optionLabel(o) {
  return /^[a-z]/.test(o) ? capitalize(o) : o;
}

function selectControl(def, value, ctx) {
  const id = ctx.id(def.key);
  const sel = h('select', { id, class: 'input select', onChange: ctx.changed });
  const render = (v) => {
    clear(sel);
    const opts = (def.options || []).map(String);
    const cur = v == null ? '' : String(v);
    if (!opts.includes(cur)) opts.unshift(cur);
    for (const o of opts) sel.append(h('option', { value: o }, o === '' ? '— Not set —' : optionLabel(o)));
    sel.value = cur;
  };
  render(value);
  return { id, el: selectWrap(sel), get: () => sel.value, set: render, ...common(sel) };
}

/** Choices shown as pictures (from the plugin: ctx.pictureBase + key/option), one to click. */
function picturesControl(def, value, ctx) {
  const id = ctx.id(def.key);
  const name = `${id}-pick`;
  const opts = (def.options || []).map(String);
  const radios = [];
  const grid = h('div', { id, class: 'pictures', role: 'radiogroup' });
  opts.forEach((o, i) => {
    const radio = h('input', { type: 'radio', name, value: o, class: 'picture-radio', onChange: ctx.changed });
    const label = `${def.label} ${i + 1}`;
    const img = h('img', { class: 'picture-img', alt: label, loading: 'lazy', draggable: 'false',
      src: ctx.pictureBase ? `${ctx.pictureBase}pictures/${encodeURIComponent(def.key)}/${encodeURIComponent(o)}` : '' });
    img.addEventListener('error', () => img.classList.add('is-missing'), { once: true });
    radios.push(radio);
    grid.append(h('label', { class: 'picture', title: label }, radio, img));
  });
  const set = (v) => {
    const cur = v == null || v === '' ? opts[0] : String(v);
    for (const r of radios) r.checked = r.value === cur;
  };
  set(value);
  return {
    id,
    el: grid,
    get: () => radios.find((r) => r.checked)?.value ?? '',
    set,
    focus: () => (radios.find((r) => r.checked) || radios[0])?.focus(),
    describe: (ids) => grid.setAttribute('aria-describedby', ids),
    invalid: (on) => grid.setAttribute('aria-invalid', on ? 'true' : 'false'),
  };
}

function boolControl(def, value, ctx) {
  const id = ctx.id(def.key);
  const input = h('input', { id, type: 'checkbox', role: 'switch', class: 'switch-input', onChange: ctx.changed });
  input.checked = !!value;
  const el = h('span', { class: 'switch' }, input, h('span', { class: 'switch-track', 'aria-hidden': 'true' }, h('span', { class: 'switch-thumb' })));
  return { id, el, get: () => input.checked, set: (v) => { input.checked = !!v; }, ...common(input) };
}

/** A standalone switch (not part of a form). */
export function switchEl({ checked = false, label, onChange, disabled = false, id = uid('sw') }) {
  const input = h('input', { id, type: 'checkbox', role: 'switch', class: 'switch-input', checked, disabled, 'aria-label': label || null });
  if (onChange) input.addEventListener('change', () => onChange(input.checked, input));
  const el = h('span', { class: 'switch' }, input, h('span', { class: 'switch-track', 'aria-hidden': 'true' }, h('span', { class: 'switch-thumb' })));
  el.input = input;
  return el;
}

function linesControl(def, value, ctx) {
  const id = ctx.id(def.key);
  const ta = h('textarea', { id, class: 'input input-lines', placeholder: def.placeholder || 'One per line', onInput: ctx.changed, spellcheck: 'false' });
  const set = (v) => {
    const arr = Array.isArray(v) ? v.map(str) : v ? [str(v)] : [];
    ta.value = arr.join('\n');
    ta.rows = Math.min(12, Math.max(3, arr.length + 1));
  };
  set(value);
  return {
    id,
    el: h('div', { class: 'lines-wrap' }, ta, h('span', { class: 'lines-hint' }, 'One per line')),
    get: () => ta.value.split('\n').map((s) => s.trim()).filter(Boolean),
    set,
    ...common(ta),
  };
}

function secretControl(def, value, ctx) {
  const id = ctx.id(def.key);
  let hasSaved = false;
  let mode = 'edit'; // saved | edit | cleared
  const input = h('input', { id, class: 'input', type: 'password', autocomplete: 'new-password', spellcheck: 'false', placeholder: def.placeholder || null, onInput: ctx.changed });
  const eye = h('button', { type: 'button', class: 'input-addon', 'aria-label': 'Show', title: 'Show', onClick: () => {
    const show = input.type === 'password';
    input.type = show ? 'text' : 'password';
    eye.replaceChildren(icon(show ? 'eye-off' : 'eye', { size: 17 }));
    eye.setAttribute('aria-label', show ? 'Hide' : 'Show');
  } }, icon('eye', { size: 17 }));
  const changeBtn = h('button', { type: 'button', class: 'btn btn-sm', onClick: () => setMode('edit', true) }, 'Change');
  const savedRow = h('div', { class: 'secret-saved' },
    icon('lock', { size: 16 }),
    h('span', { class: 'secret-mask', 'aria-label': 'saved secret' }, MASK),
    h('span', { class: 'secret-note' }, 'Saved'),
    h('span', { class: 'secret-actions' }, changeBtn, h('button', { type: 'button', class: 'btn btn-sm btn-ghost', onClick: () => setMode('cleared') }, 'Remove')));
  const keepBtn = h('button', { type: 'button', class: 'linkish', onClick: () => setMode('saved') }, 'Keep the saved one');
  const editRow = h('div', { class: 'secret-edit' }, h('div', { class: 'input-group' }, input, eye), keepBtn);
  const clearedRow = h('div', { class: 'secret-cleared' }, icon('trash', { size: 16 }), h('span', null, 'Will be removed when you save.'), h('button', { type: 'button', class: 'linkish', onClick: () => setMode('saved') }, 'Undo'));
  const reentry = h('p', { class: 'secret-reentry', hidden: true }, 'Please enter this again: saved secrets are matched by their place in the list, and a row above was removed.');
  const el = h('div', { class: 'secret' }, savedRow, editRow, clearedRow, reentry);

  function setMode(m, focus = false) {
    mode = m;
    savedRow.hidden = m !== 'saved';
    editRow.hidden = m !== 'edit';
    clearedRow.hidden = m !== 'cleared';
    keepBtn.hidden = !hasSaved;
    if (m !== 'edit') input.value = '';
    ctx.changed();
    if (focus) setTimeout(() => input.focus(), 0);
  }
  function set(v) {
    hasSaved = typeof v === 'string' && v.length > 0;
    mode = hasSaved ? 'saved' : 'edit';
    input.value = '';
    input.placeholder = hasSaved ? 'Type a new value' : def.placeholder || '';
    reentry.hidden = true;
    savedRow.hidden = mode !== 'saved';
    editRow.hidden = mode !== 'edit';
    clearedRow.hidden = true;
    keepBtn.hidden = !hasSaved;
  }
  set(value);
  return {
    id,
    el,
    get() {
      if (mode === 'saved') return MASK;
      if (mode === 'cleared') return '';
      return input.value === '' && hasSaved ? MASK : input.value;
    },
    set,
    focus: () => (mode === 'saved' ? changeBtn : input).focus(),
    describe: (ids) => input.setAttribute('aria-describedby', ids),
    invalid: (on) => input.setAttribute('aria-invalid', on ? 'true' : 'false'),
    invalidateSaved() {
      if (!hasSaved) return;
      hasSaved = false;
      reentry.hidden = false;
      setMode('edit');
    },
  };
}

function singular(label) {
  let s = String(label || 'item').trim();
  if (/ies$/i.test(s)) s = s.replace(/ies$/i, 'y');
  else if (/(s|x|ch|sh)es$/i.test(s)) s = s.replace(/es$/i, '');
  else if (/[^s]s$/i.test(s)) s = s.slice(0, -1);
  const first = s.split(' ')[0];
  if (first !== first.toUpperCase()) s = s.charAt(0).toLowerCase() + s.slice(1);
  return s;
}

function listControl(def, value, ctx) {
  const id = ctx.id(def.key);
  const itemFields = def.item?.fields || [];
  const simple = itemFields.length > 0 && itemFields.length <= 4
    && itemFields.every((f) => ['string', 'int', 'number', 'bool', 'select'].includes(f.type || 'string') && !f.advanced && !f.secret);
  const name = singular(def.label);
  const rows = [];
  let seq = 0;

  const rowsEl = h('div', { class: 'list-rows' });
  const emptyEl = h('p', { class: 'list-empty' }, `No ${def.label ? def.label.toLowerCase() : 'items'} yet.`);
  const addBtn = h('button', { type: 'button', class: 'btn btn-sm', onClick: () => {
    const r = addRow(null, true);
    ctx.changed();
    r.focus();
  } }, icon('plus', { size: 16 }), `Add ${name}`);
  const colWidth = (f) => ({ bool: '64px', select: 'minmax(0, 0.8fr)', int: 'minmax(0, 0.55fr)', number: 'minmax(0, 0.55fr)' }[f.type] || 'minmax(0, 1fr)');
  const grid = `${itemFields.map(colWidth).join(' ')} 36px`;
  const head = simple
    ? h('div', { class: 'list-head', 'aria-hidden': 'true', style: { '--grid': grid } }, itemFields.map((f) => h('span', null, f.label)), h('span'))
    : null;
  const el = h('div', { class: ['list-field', simple ? 'list-simple' : 'list-cards'], id }, head, rowsEl, emptyEl, h('div', { class: 'list-add' }, addBtn));

  function rowTitle(row) {
    const c = row.controls.find((x) => (x.def.type || 'string') === 'string' && !x.def.secret && String(x.control.get() ?? '').trim());
    return c ? String(c.control.get()).trim() : '';
  }

  function refresh() {
    emptyEl.hidden = rows.length > 0;
    if (head) head.hidden = rows.length === 0;
    rows.forEach((r) => r.updateTitle?.());
  }

  function addRow(v, isNew = false) {
    seq += 1;
    const n = seq;
    const orig = v && typeof v === 'object' ? JSON.parse(JSON.stringify(v)) : {};
    const row = { orig, controls: [], el: null, updateTitle: null };
    const rctx = {
      id: (k) => `${id}-${n}-${k}`,
      changed: () => { row.updateTitle?.(); ctx.changed(); },
      custom: null,
    };
    const main = [];
    const adv = [];
    for (const fd of itemFields) {
      let initialValue = v ? v[fd.key] : undefined;
      if (!v && fd.type === 'select' && fd.options?.length) initialValue = fd.options[0];
      if (!v && fd.type === 'bool') initialValue = false;
      const control = makeControl(fd, initialValue, rctx);
      const { wrap, err } = fieldWrap(fd, control);
      row.controls.push({ def: fd, control, wrap, err });
      (fd.advanced ? adv : main).push(wrap);
    }
    const remove = h('button', {
      type: 'button', class: 'icon-btn list-remove', title: `Remove this ${name}`, 'aria-label': `Remove this ${name}`,
      onClick: (e) => { e.preventDefault(); e.stopPropagation(); removeRow(row); },
    }, icon('trash', { size: 17 }));
    if (simple) {
      row.el = h('div', { class: 'list-row', style: { '--grid': grid } }, main, h('div', { class: 'list-row-remove' }, remove));
    } else {
      const title = h('span', { class: 'list-card-title' });
      row.updateTitle = () => { title.textContent = rowTitle(row) || `${capitalize(name)} ${rows.indexOf(row) + 1}`; };
      row.el = h('details', { class: 'list-card', open: isNew || !v || (value || []).length <= 3 },
        h('summary', null, icon('chevron-right', { size: 16, cls: 'chev' }), title, remove),
        h('div', { class: 'list-card-body' },
          main,
          adv.length ? h('details', { class: 'sform-advanced' }, h('summary', null, icon('chevron-right', { size: 16, cls: 'chev' }), h('span', null, 'More')), h('div', { class: 'sform-fields' }, adv)) : null));
    }
    row.focus = () => {
      if (row.el.tagName === 'DETAILS') row.el.open = true;
      row.controls[0]?.control.focus?.();
    };
    rows.push(row);
    rowsEl.append(row.el);
    refresh();
    return row;
  }

  function removeRow(row) {
    const idx = rows.indexOf(row);
    if (idx < 0) return;
    rows.splice(idx, 1);
    row.el.remove();
    row.controls.forEach((c) => c.control.destroy?.());
    // The hub matches saved secrets by position, so rows below this one can't keep theirs.
    for (let j = idx; j < rows.length; j++) rows[j].controls.forEach((c) => c.control.invalidateSaved?.());
    refresh();
    ctx.changed();
  }

  function set(v) {
    rows.splice(0).forEach((r) => { r.el.remove(); r.controls.forEach((c) => c.control.destroy?.()); });
    (Array.isArray(v) ? v : []).forEach((item) => addRow(item));
    refresh();
  }
  set(value);

  return {
    id,
    el,
    get: () => rows.map((r) => {
      const o = JSON.parse(JSON.stringify(r.orig));
      for (const { def: fd, control } of r.controls) {
        const val = control.get();
        // New rows: leave empty fields out so the hub uses its defaults
        if ((val === '' || val === null) && !(fd.key in r.orig)) {
          delete o[fd.key];
          continue;
        }
        o[fd.key] = val;
      }
      return o;
    }),
    set,
    validate() {
      let first = null;
      for (const r of rows) {
        for (const c of r.controls) {
          const msg = c.control.validate?.() || null;
          c.err.textContent = msg || '';
          c.err.hidden = !msg;
          c.wrap.classList.toggle('has-error', !!msg);
          c.control.invalid?.(!!msg);
          if (msg && !first) {
            first = msg;
            if (r.el.tagName === 'DETAILS') r.el.open = true;
          }
        }
      }
      return first ? `Check the ${name} above: ${first.charAt(0).toLowerCase()}${first.slice(1)}` : null;
    },
    focus: () => (rows[0] ? rows[0].focus() : addBtn.focus()),
    destroy: () => rows.forEach((r) => r.controls.forEach((c) => c.control.destroy?.())),
  };
}

// ------------------------------------------------------------------ custom controls pages use

/** A slider for a number with a known range. */
export function rangeControl({ min, max, step = 1, unit = '', format }) {
  return (def, value, ctx) => {
    const id = ctx.id(def.key);
    const isInt = def.type === 'int';
    const range = h('input', { id, type: 'range', class: 'range', min, max, step });
    const out = h('output', { class: 'range-out', for: id });
    const sync = () => {
      const n = Number(range.value);
      out.textContent = format ? format(n) : `${n}${unit}`;
      range.style.setProperty('--pct', `${((n - min) / (max - min)) * 100}%`);
    };
    range.addEventListener('input', () => { sync(); ctx.changed(); });
    const set = (v) => {
      range.value = String(v == null ? min : v);
      sync();
    };
    set(value);
    return {
      id,
      el: h('div', { class: 'range-wrap' }, range, out),
      get: () => (isInt ? Math.round(Number(range.value)) : Number(range.value)),
      set,
      focus: () => range.focus(),
      describe: (ids) => range.setAttribute('aria-describedby', ids),
    };
  };
}

/**
 * A dropdown whose choices come from the hub's live state.
 * getOptions() → [{value, label, disabled?}]; subscribe(render) → unsubscribe.
 */
export function choiceControl(getOptions, { subscribe, onPick, missingLabel = (v) => `${v} (not available right now)` } = {}) {
  return (def, value, ctx) => {
    const id = ctx.id(def.key);
    const sel = h('select', { id, class: 'input select' });
    let current = value == null ? '' : String(value);
    let lastSig = null;
    const render = () => {
      const keep = current;
      const opts = getOptions().slice();
      const match = opts.find((o) => o.value.toLowerCase() === keep.toLowerCase());
      if (!match) opts.push({ value: keep, label: keep === '' ? '— Not set —' : missingLabel(keep) });
      // Rebuilding the options closes an open dropdown, so only do it when they changed
      const sig = JSON.stringify([opts, keep]);
      if (sig !== lastSig) {
        lastSig = sig;
        clear(sel);
        for (const o of opts) sel.append(h('option', { value: o.value, disabled: o.disabled || null }, o.label));
        sel.value = match ? match.value : keep;
      }
      onPick?.(sel.value);
    };
    sel.addEventListener('change', () => {
      current = sel.value;
      lastSig = null;
      onPick?.(sel.value);
      ctx.changed();
    });
    render();
    const off = subscribe ? subscribe(render) : null;
    return {
      id,
      el: selectWrap(sel),
      select: sel,
      get: () => current,
      set: (v) => { current = v == null ? '' : String(v); lastSig = null; render(); },
      focus: () => sel.focus(),
      describe: (ids) => sel.setAttribute('aria-describedby', ids),
      destroy: () => off?.(),
    };
  };
}
