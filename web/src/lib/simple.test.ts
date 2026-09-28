import { describe, expect, it } from 'vitest';
import { project, instance, sampleProjects, svc } from '../test/fixtures';
import { activeProjectNames, enterAction, finderHits, pruneTabs, summaryTone, withNewlyActive } from './simple';

describe('simple mode helpers', () => {
  it('lists every project main checkout for an empty query, disabled services hidden', () => {
    const hits = finderHits(sampleProjects(), '');
    expect(hits.map((h) => h.instance.key)).toEqual(['other', 'panel-pro']);
    expect(hits[1].services.map((s) => s.name)).toEqual(['api', 'web']);
  });

  it('narrows a service-name match to that service', () => {
    const hits = finderHits(sampleProjects(), 'web');
    expect(hits).toHaveLength(1);
    expect(hits[0].services.map((s) => s.name)).toEqual(['web']);
  });

  it('Enter starts a single stopped match, else opens the first repo', () => {
    const ps = sampleProjects();
    expect(enterAction(finderHits(ps, 'web'), 'web')).toMatchObject({ kind: 'start', service: { name: 'web' } });
    // api is running in main and stopped in the worktree: two matches -> open repo
    expect(enterAction(finderHits(ps, 'api'), 'api')).toEqual({ kind: 'open', project: 'panel-pro' });
    expect(enterAction(finderHits(ps, ''), '')).toBeNull();
  });

  it('adds newly active projects to the tabs once', () => {
    const ps = sampleProjects();
    const active = activeProjectNames(ps);
    expect(active).toEqual(['panel-pro', 'other']);
    expect(withNewlyActive([], null, active)).toEqual(['panel-pro', 'other']);
    // closed tab does not come back while the project stays active
    expect(withNewlyActive(['other'], active, active)).toEqual(['other']);
  });

  it('prunes removed projects and fixes case', () => {
    const tabs = ['Panel-Pro', 'gone'];
    expect(pruneTabs(tabs, sampleProjects())).toEqual(['panel-pro']);
    const same = ['other'];
    expect(pruneTabs(same, sampleProjects())).toBe(same);
  });

  it('summarises tone with failures first', () => {
    const p = project('x', [instance('x', [svc('x', 'a', { state: 'running' }), svc('x', 'b', { state: 'crashed' })])]);
    expect(summaryTone(p.instances[0].services)).toBe('bad');
    expect(summaryTone([])).toBe('off');
  });
});
