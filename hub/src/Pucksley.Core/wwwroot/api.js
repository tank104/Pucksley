// Talking to the hub's REST API. Every change carries X-Puck-Ui: 1 (the hub's CSRF guard).

export class ApiError extends Error {
  constructor(message, status = 0, body = null) {
    super(message);
    this.status = status;
    this.body = body;
  }
}

let unauthorized = null;

/** Called once when the hub answers 401 (the page needs the admin key). */
export function onUnauthorized(fn) {
  unauthorized = fn;
}

function friendly(status, body, text) {
  const msg = body && typeof body === 'object' ? body.error || body.message || body.title : null;
  if (msg) return msg;
  if (status === 404) return 'The hub doesn’t know about that (it may have just gone away).';
  if (status === 403) return 'The hub refused that request.';
  if (status >= 500) return 'Something went wrong inside the hub. The Activity page’s log may say more.';
  if (text && text.length < 200) return text;
  return `The hub answered with an error (${status}).`;
}

/**
 * request('POST', '/api/chat', { json: {...} })
 * options: json, body (Blob/ArrayBuffer), contentType, timeout (ms), signal
 * Returns parsed JSON, a Blob for audio, text, or null for an empty body.
 */
export async function request(method, path, { json, body, contentType, timeout = 0, signal } = {}) {
  const headers = { Accept: 'application/json' };
  let payload;
  if (method !== 'GET' && method !== 'HEAD') headers['X-Puck-Ui'] = '1';
  if (body !== undefined) {
    payload = body;
    headers['Content-Type'] = contentType || 'application/octet-stream';
  } else if (json !== undefined) {
    payload = JSON.stringify(json);
    headers['Content-Type'] = 'application/json';
  } else if (method !== 'GET' && method !== 'HEAD' && method !== 'DELETE') {
    payload = '{}';
    headers['Content-Type'] = 'application/json';
  }

  const ctrl = new AbortController();
  let timer = null;
  if (timeout) timer = setTimeout(() => ctrl.abort(), timeout);
  if (signal) {
    if (signal.aborted) ctrl.abort();
    else signal.addEventListener('abort', () => ctrl.abort(), { once: true });
  }

  let res;
  try {
    res = await fetch(path, { method, headers, body: payload, credentials: 'same-origin', cache: 'no-store', signal: ctrl.signal });
  } catch (e) {
    if (ctrl.signal.aborted && !signal?.aborted) throw new ApiError('The hub took too long to answer.', 0);
    if (signal?.aborted) throw new ApiError('Cancelled.', -1);
    throw new ApiError('Can’t reach the hub. Is it running?', 0);
  } finally {
    if (timer) clearTimeout(timer);
  }

  if (res.status === 401) {
    unauthorized?.();
    throw new ApiError('This page needs the hub’s admin key.', 401);
  }

  const type = res.headers.get('content-type') || '';
  if (!res.ok) {
    let parsed = null;
    let text = '';
    try {
      text = await res.text();
      parsed = text && type.includes('json') ? JSON.parse(text) : null;
    } catch { /* not JSON */ }
    throw new ApiError(friendly(res.status, parsed, text), res.status, parsed);
  }

  if (type.startsWith('audio/') || type.startsWith('application/octet-stream')) return res.blob();
  const text = await res.text();
  if (!text) return null;
  if (type.includes('json')) {
    try { return JSON.parse(text); } catch { return text; }
  }
  return text;
}

export const get = (path, opts) => request('GET', path, opts);
export const post = (path, json, opts) => request('POST', path, { ...opts, json });
export const put = (path, json, opts) => request('PUT', path, { ...opts, json });
export const del = (path, opts) => request('DELETE', path, opts);

export const enc = encodeURIComponent;
