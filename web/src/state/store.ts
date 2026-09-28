import { useSyncExternalStore } from 'react';
import { initialState, reducer, type Action, type AppState } from './reducer';

export interface Store {
  getState(): AppState;
  dispatch(action: Action): void;
  subscribe(listener: () => void): () => void;
}

export function createStore(initial: AppState): Store {
  let state = initial;
  const listeners = new Set<() => void>();
  return {
    getState: () => state,
    dispatch(action) {
      const next = reducer(state, action);
      if (next === state) return;
      state = next;
      for (const l of listeners) l();
    },
    subscribe(l) {
      listeners.add(l);
      return () => listeners.delete(l);
    },
  };
}

const VIEW_KEY = 'repomanager.instance';

function persistedInstance(): string | null {
  try {
    return window.sessionStorage.getItem(VIEW_KEY);
  } catch {
    return null;
  }
}

export const store: Store = createStore(initialState(typeof window === 'undefined' ? null : persistedInstance()));

// Remember the selected instance across reloads (per window session).
let lastKey: string | null = null;
store.subscribe(() => {
  const v = store.getState().view;
  const key = v.kind === 'instance' ? v.key : null;
  if (key === lastKey) return;
  lastKey = key;
  try {
    if (key) window.sessionStorage.setItem(VIEW_KEY, key);
  } catch {
    /* ignore */
  }
});

/** Subscribes a component to a slice of the app state. Selectors must return stable references. */
export function useAppState<T>(selector: (s: AppState) => T): T {
  return useSyncExternalStore(store.subscribe, () => selector(store.getState()), () => selector(store.getState()));
}

export const dispatch = (a: Action) => store.dispatch(a);
