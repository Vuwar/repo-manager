import { describe, expect, it } from 'vitest';
import type { ActionResultDto } from '../api/types';
import { retryActions } from './actions';

const failed = (fixes?: ActionResultDto['fixes']): ActionResultDto => ({ ok: false, error: 'x', affected: [], logTail: [], fixes });

describe('retryActions', () => {
  it('offers a button per fix the daemon suggests, in its order', () => {
    const labels = retryActions('start', 'p/web', failed(['kill-owner', 'new-port']))?.map((a) => a.label);
    expect(labels).toEqual(['Kill owner & retry', 'Start on new port']);
  });

  it('offers only Edit config when the command ignores its port', () => {
    expect(retryActions('start', 'p/web', failed(['edit-config']))?.map((a) => a.label)).toEqual(['Edit config']);
  });

  it('offers nothing without fixes, whatever the error text says', () => {
    expect(retryActions('start', 'p/web', { ...failed(), error: 'port 5173 not listening' })).toBeUndefined();
  });
});
