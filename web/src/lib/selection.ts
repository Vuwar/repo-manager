export type SelectMode = 'single' | 'toggle' | 'range';

export function modeFromEvent(e: { ctrlKey: boolean; metaKey: boolean; shiftKey: boolean }): SelectMode {
  if (e.shiftKey) return 'range';
  if (e.ctrlKey || e.metaKey) return 'toggle';
  return 'single';
}

/**
 * Next selection after clicking `id` in `order` (the visible list).
 * single: just id; toggle: add/remove id; range: anchor..id added to what is selected.
 * Items selected elsewhere (other lists) are kept for toggle/range.
 */
export function nextSelection(order: string[], selected: string[], id: string, mode: SelectMode, anchor: string | null): string[] {
  if (mode === 'single') return [id];
  if (mode === 'toggle') return selected.includes(id) ? selected.filter((x) => x !== id) : [...selected, id];
  const a = anchor && order.includes(anchor) ? order.indexOf(anchor) : order.indexOf(id);
  const b = order.indexOf(id);
  const [lo, hi] = a <= b ? [a, b] : [b, a];
  const range = order.slice(lo, hi + 1);
  return [...selected.filter((x) => !range.includes(x)), ...range];
}
