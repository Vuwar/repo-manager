const KEY = 'repomanager.token';

function safeSession(): Storage | null {
  try {
    return window.sessionStorage;
  } catch {
    return null;
  }
}

/**
 * The host opens the UI at http://127.0.0.1:4000/#token=<token>.
 * Take the token from the hash, keep it in sessionStorage (so reloads work) and strip it from the URL.
 */
export function initToken(): string | null {
  const hash = window.location.hash.replace(/^#/, '');
  const params = new URLSearchParams(hash);
  const fromHash = params.get('token');
  const store = safeSession();
  if (fromHash) {
    try {
      store?.setItem(KEY, fromHash);
    } catch {
      /* storage unavailable: token lives in memory only */
    }
    params.delete('token');
    const rest = params.toString();
    const url = window.location.pathname + window.location.search + (rest ? '#' + rest : '');
    try {
      window.history.replaceState(null, '', url);
    } catch {
      /* ignore */
    }
    return fromHash;
  }
  try {
    return store?.getItem(KEY) ?? null;
  } catch {
    return null;
  }
}

export function clearToken(): void {
  try {
    safeSession()?.removeItem(KEY);
  } catch {
    /* ignore */
  }
}
