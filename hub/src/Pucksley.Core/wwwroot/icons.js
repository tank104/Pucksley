// Inline SVG icons (24×24, stroked). Static strings only.

const P = {
  home: '<path d="M3.5 10.5 12 3.5l8.5 7"/><path d="M5.5 9v10.5a1 1 0 0 0 1 1H10v-6h4v6h3.5a1 1 0 0 0 1-1V9"/>',
  talk: '<path d="M20.5 11.5a8 8 0 0 1-11.7 7.1L4 20l1.4-4.4a8 8 0 1 1 15.1-4.1Z"/>',
  puck: '<circle cx="12" cy="12" r="8.5"/><circle cx="12" cy="12" r="4.5"/>',
  plugins: '<rect x="3.5" y="3.5" width="7" height="7" rx="1.8"/><rect x="13.5" y="3.5" width="7" height="7" rx="1.8"/><rect x="3.5" y="13.5" width="7" height="7" rx="1.8"/><path d="M17 13.5v7M13.5 17h7"/>',
  sparkle: '<path d="M11 3.5l1.8 4.7 4.7 1.8-4.7 1.8L11 16.5l-1.8-4.7L4.5 10l4.7-1.8L11 3.5Z"/><path d="M18.5 14.5l.8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8.8-2.2Z"/>',
  voice: '<path d="M4 10v4M8 6.5v11M12 3.5v17M16 7.5v9M20 10v4"/>',
  household: '<circle cx="9" cy="8" r="3.5"/><path d="M2.5 20a6.5 6.5 0 0 1 13 0"/><path d="M15.5 4.6a3.5 3.5 0 0 1 0 6.8"/><path d="M18 14.3a6.5 6.5 0 0 1 3.5 5.7"/>',
  activity: '<path d="M3 12h4l2.5-7 5 14 2.5-7h4"/>',
  sun: '<circle cx="12" cy="12" r="4"/><path d="M12 2.5v2M12 19.5v2M4.6 4.6 6 6M18 18l1.4 1.4M2.5 12h2M19.5 12h2M4.6 19.4 6 18M18 6l1.4-1.4"/>',
  moon: '<path d="M20 14.2A8 8 0 1 1 9.8 4a6.4 6.4 0 0 0 10.2 10.2Z"/>',
  auto: '<circle cx="12" cy="12" r="8.5"/><path d="M12 3.5a8.5 8.5 0 0 1 0 17Z" fill="currentColor" stroke="none"/>',
  mic: '<rect x="9" y="3" width="6" height="11.5" rx="3"/><path d="M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21"/>',
  ear: '<path d="M6.5 9a5.5 5.5 0 0 1 11 0c0 3-2.2 4.2-2.9 6.4-.5 1.6-1.1 3.6-3.6 3.6a3.2 3.2 0 0 1-3-2"/><path d="M9.5 9.5a2.5 2.5 0 0 1 5 0c0 1.4-1.5 1.8-1.5 3.2"/>',
  speaker: '<path d="M11 5 6.5 8.5H3.5v7h3L11 19V5Z"/><path d="M15.5 9a4.5 4.5 0 0 1 0 6M18.5 6a8.5 8.5 0 0 1 0 12"/>',
  send: '<path d="M12 19V5.5M5.5 12 12 5.5l6.5 6.5"/>',
  plus: '<path d="M12 5v14M5 12h14"/>',
  trash: '<path d="M4 7h16M10 11v6M14 11v6M6 7l.9 12.1a1 1 0 0 0 1 .9h8.2a1 1 0 0 0 1-.9L18 7M9 7V4.5h6V7"/>',
  x: '<path d="M6 6l12 12M18 6 6 18"/>',
  check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
  'check-circle': '<circle cx="12" cy="12" r="8.5"/><path d="M8.2 12.4l2.6 2.6 5-5.3"/>',
  'chevron-down': '<path d="M6 9l6 6 6-6"/>',
  'chevron-right': '<path d="M9 6l6 6-6 6"/>',
  'chevron-left': '<path d="M15 6l-6 6 6 6"/>',
  sliders: '<path d="M4 6h9M17 6h3M4 12h3M11 12h9M4 18h11M19 18h1"/><circle cx="15" cy="6" r="2"/><circle cx="9" cy="12" r="2"/><circle cx="17" cy="18" r="2"/>',
  wifi: '<path d="M2.5 9a14 14 0 0 1 19 0"/><path d="M5.5 12.3a9.5 9.5 0 0 1 13 0"/><path d="M8.7 15.5a5 5 0 0 1 6.6 0"/><path d="M12 19h.01"/>',
  usb: '<rect x="7.5" y="2.5" width="9" height="7" rx="1"/><path d="M10.5 5.5h.01M13.5 5.5h.01"/><path d="M8.5 9.5v3.5a3.5 3.5 0 0 0 7 0V9.5M12 16.5v5"/>',
  bolt: '<path d="M13 2.5 5 13.5h6l-1 8 8-11h-6l1-8Z"/>',
  battery: '<rect x="2.5" y="7" width="16.5" height="10" rx="2"/><path d="M21.5 10.5v3"/>',
  refresh: '<path d="M19.5 10A8 8 0 0 0 5 7.5L3.5 9"/><path d="M3.5 4v5h5"/><path d="M4.5 14A8 8 0 0 0 19 16.5l1.5-1.5"/><path d="M20.5 20v-5h-5"/>',
  external: '<path d="M14 4h6v6M20 4l-9 9"/><path d="M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/>',
  alert: '<path d="M10.3 4.3 2.6 17.6A2 2 0 0 0 4.3 20.5h15.4a2 2 0 0 0 1.7-2.9L13.7 4.3a2 2 0 0 0-3.4 0Z"/><path d="M12 9.5v4M12 17h.01"/>',
  info: '<circle cx="12" cy="12" r="8.5"/><path d="M12 11v5M12 8h.01"/>',
  play: '<path d="M7.5 4.8v14.4a.8.8 0 0 0 1.2.7l11-7.2a.8.8 0 0 0 0-1.4l-11-7.2a.8.8 0 0 0-1.2.7Z"/>',
  stop: '<rect x="6" y="6" width="12" height="12" rx="2"/>',
  pause: '<path d="M8.5 5v14M15.5 5v14"/>',
  key: '<circle cx="8" cy="15.5" r="4"/><path d="M11 12.5 19.5 4M16.5 7l2.5 2.5M14.5 9l2 2"/>',
  pin: '<path d="M12 21s6.5-5.8 6.5-11A6.5 6.5 0 0 0 5.5 10c0 5.2 6.5 11 6.5 11Z"/><circle cx="12" cy="10" r="2.3"/>',
  clock: '<circle cx="12" cy="12" r="8.5"/><path d="M12 7.5V12l3 2"/>',
  tool: '<path d="M14.6 6.4a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.4-3.4a5.5 5.5 0 0 1-7.4 7.4l-6.3 6.3a2 2 0 0 1-2.9-2.9l6.3-6.3a5.5 5.5 0 0 1 7.4-7.4l-3.4 3.3Z"/>',
  music: '<path d="M9 18V5.5l10.5-2v12.5"/><circle cx="6.5" cy="18" r="2.5"/><circle cx="17" cy="16" r="2.5"/>',
  volume: '<path d="M11 5 6.5 8.5H3.5v7h3L11 19V5Z"/><path d="M15.5 9a4.5 4.5 0 0 1 0 6M18.5 6a8.5 8.5 0 0 1 0 12"/>',
  heat: '<path d="M10 14.2V5a2 2 0 0 1 4 0v9.2a4 4 0 1 1-4 0Z"/><path d="M12 10.5v6"/>',
  light: '<path d="M9.5 18h5M10.5 21h3"/><path d="M12 3a6 6 0 0 0-3.6 10.8c.7.5 1.1 1.3 1.1 2.1v.1h5v-.1c0-.8.4-1.6 1.1-2.1A6 6 0 0 0 12 3Z"/>',
  tv: '<rect x="3" y="6.5" width="18" height="12" rx="2"/><path d="M8.5 21.5h7M8.5 2.5 12 6l3.5-3.5"/>',
  plug: '<path d="M9 2.5v5M15 2.5v5"/><path d="M6.5 7.5h11V11a5.5 5.5 0 0 1-11 0V7.5Z"/><path d="M12 16.5v5"/>',
  search: '<circle cx="11" cy="11" r="6.5"/><path d="M20 20l-4.3-4.3"/>',
  timer: '<circle cx="12" cy="13.5" r="7.5"/><path d="M12 10v3.5l2.5 2M9.5 2.5h5M12 2.5V6"/>',
  pc: '<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8.5 20h7M12 16v4"/>',
  note: '<path d="M6 3h8.5L19 7.5V20a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1Z"/><path d="M14.5 3v4.5H19M8.5 12.5h7M8.5 16h5"/>',
  weather: '<path d="M8 2.5v1.5M3.2 4.7l1 1M12.8 4.7l-1 1M2 9.5h1.5"/><path d="M5.3 11.4A3.3 3.3 0 1 1 10.6 7"/><path d="M8.5 20.5h9a3.5 3.5 0 0 0 .3-7 5 5 0 0 0-9.6 1.2 2.9 2.9 0 0 0 .3 5.8Z"/>',
  menu: '<path d="M4 7h16M4 12h16M4 17h16"/>',
  copy: '<rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5a1 1 0 0 1 1-1h10"/>',
  power: '<path d="M12 3v8.5"/><path d="M6.4 6.6a8 8 0 1 0 11.2 0"/>',
  eye: '<path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z"/><circle cx="12" cy="12" r="2.8"/>',
  'eye-off': '<path d="M3 3l18 18"/><path d="M10.6 5.6a9.3 9.3 0 0 1 1.4-.1c6 0 9.5 6.5 9.5 6.5a16 16 0 0 1-2.7 3.5M6.5 6.9C3.9 8.6 2.5 12 2.5 12S6 18.5 12 18.5c1.6 0 3-.4 4.2-1.1"/><path d="M9.9 9.9a3 3 0 0 0 4.2 4.2"/>',
  lock: '<rect x="4.5" y="10.5" width="15" height="10" rx="2"/><path d="M8 10.5V7.5a4 4 0 0 1 8 0v3"/>',
  bell: '<path d="M6 9a6 6 0 0 1 12 0c0 6.5 2.5 8 2.5 8h-17S6 15.5 6 9Z"/><path d="M10.2 20.5a2 2 0 0 0 3.6 0"/>',
  user: '<circle cx="12" cy="8" r="4"/><path d="M4.5 20.5a7.5 7.5 0 0 1 15 0"/>',
  globe: '<circle cx="12" cy="12" r="8.5"/><path d="M3.5 12h17M12 3.5a13 13 0 0 1 0 17M12 3.5a13 13 0 0 0 0 17"/>',
  link: '<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1"/><path d="M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/>',
  'new-chat': '<path d="M20.5 11.5a8 8 0 0 1-11.7 7.1L4 20l1.4-4.4a8 8 0 1 1 15.1-4.1Z"/><path d="M12.5 8.5v6M9.5 11.5h6"/>',
  'arrow-down': '<path d="M12 5v14M5.5 12.5 12 19l6.5-6.5"/>',
  'arrow-right': '<path d="M5 12h14M12.5 5.5 19 12l-6.5 6.5"/>',
  dots: '<path d="M6 12h.01M12 12h.01M18 12h.01"/>',
  code: '<path d="M8.5 7 3.5 12l5 5M15.5 7l5 5-5 5"/>',
  shield: '<path d="M12 3 5 6v5.5c0 4.4 3 7.9 7 9.5 4-1.6 7-5.1 7-9.5V6l-7-3Z"/>',
  package: '<path d="M12 3 4 7v10l8 4 8-4V7l-8-4Z"/><path d="M4 7l8 4 8-4M12 11v10"/>',
};

const STROKE = 1.8;

/** An SVG icon element. Unknown names fall back to "tool". */
export function icon(name, { size = 18, cls = '', label = null, stroke = STROKE } = {}) {
  const paths = P[name] || P.tool;
  const tpl = document.createElement('template');
  tpl.innerHTML = `<svg class="icon ${cls}" width="${size}" height="${size}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="${stroke}" stroke-linecap="round" stroke-linejoin="round"${label ? ` role="img" aria-label="${label.replace(/"/g, '&quot;')}"` : ' aria-hidden="true" focusable="false"'}>${paths}</svg>`;
  return tpl.content.firstElementChild;
}

export function hasIcon(name) {
  return Object.prototype.hasOwnProperty.call(P, name);
}

/** Plugin icon names (from IPlugin.Icon) → our icon set. */
export function pluginIconName(name) {
  const known = ['music', 'volume', 'heat', 'light', 'tv', 'plug', 'search', 'timer', 'pc', 'home', 'note', 'weather', 'tool'];
  if (known.includes(name)) return name;
  if (name === 'mic' || name === 'voice' || name === 'speech') return 'voice';
  return hasIcon(name) ? name : 'tool';
}
