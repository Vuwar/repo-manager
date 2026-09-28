import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { sampleProjects } from '../test/fixtures';
import { ServicesTable } from './ServicesTable';

function setup(opts: { worktree?: boolean; selected?: string[]; pending?: Record<string, string> } = {}) {
  const projects = sampleProjects();
  const inst = opts.worktree ? projects[0].instances[1] : projects[0].instances[0];
  const onAction = vi.fn();
  const onSelectionChange = vi.fn();
  render(
    <ServicesTable
      services={inst.services}
      isWorktree={!!opts.worktree}
      selected={opts.selected ?? []}
      pending={opts.pending ?? {}}
      onAction={onAction}
      onSelectionChange={onSelectionChange}
    />,
  );
  return { onAction, onSelectionChange };
}

describe('ServicesTable', () => {
  it('shows state badges, port links, uptime and markers', () => {
    setup();
    const api = screen.getByTestId('svc-api');
    expect(within(api).getByText('Running')).toBeTruthy();
    const link = within(api).getByRole('link');
    expect(link.getAttribute('href')).toBe('http://127.0.0.1:5080');
    expect(link.getAttribute('target')).toBe('_blank');
    expect(within(api).getByText(/^1m 0[45]s$/)).toBeTruthy();
    expect(within(api).getByText('auto')).toBeTruthy();
    expect(within(api).getByLabelText('Health check passing')).toBeTruthy();
    expect(within(api).getByText('2')).toBeTruthy();

    const web = screen.getByTestId('svc-web');
    expect(within(web).getByText('Failed')).toBeTruthy();
    // lastError row under a failed service
    expect(screen.getByText('port 5173 is in use by node.exe (PID 999)')).toBeTruthy();
    expect(screen.getByText('exit 1')).toBeTruthy();

    const docs = screen.getByTestId('svc-docs');
    expect(docs.className).toContain('disabled');
    expect(within(docs).getByText('disabled')).toBeTruthy();
  });

  it('offers Stop for running services and Start for stopped ones', async () => {
    const { onAction } = setup();
    const user = userEvent.setup();
    expect(screen.queryByRole('button', { name: 'Start api' })).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Stop api' }));
    expect(onAction).toHaveBeenCalledWith('stop', 'panel-pro/api');

    await user.click(screen.getByRole('button', { name: 'Start web' }));
    expect(onAction).toHaveBeenCalledWith('start', 'panel-pro/web');

    await user.click(screen.getByRole('button', { name: 'Restart web' }));
    expect(onAction).toHaveBeenCalledWith('restart', 'panel-pro/web');

    const startDocs = screen.getByRole('button', { name: 'Start docs' }) as HTMLButtonElement;
    expect(startDocs.disabled).toBe(true);
  });

  it('disables buttons of a service with a pending action', () => {
    setup({ pending: { 'panel-pro/api': 'Stopping' } });
    expect((screen.getByRole('button', { name: 'Stop api' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Restart api' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('selects rows for the log pane (click, ctrl+click, checkbox)', async () => {
    const { onSelectionChange } = setup({ selected: ['panel-pro/api'] });
    const user = userEvent.setup();
    await user.click(within(screen.getByTestId('svc-web')).getByText('web'));
    expect(onSelectionChange).toHaveBeenLastCalledWith(['panel-pro/web']);

    await user.keyboard('{Control>}');
    await user.click(within(screen.getByTestId('svc-web')).getByText('web'));
    await user.keyboard('{/Control}');
    expect(onSelectionChange).toHaveBeenLastCalledWith(['panel-pro/api', 'panel-pro/web']);

    await user.click(screen.getByRole('checkbox', { name: 'Show logs of api' }));
    expect(onSelectionChange).toHaveBeenLastCalledWith([]);
  });

  it('warns about services that are not worktree-ready', () => {
    setup({ worktree: true });
    expect(screen.getByText('not worktree-ready')).toBeTruthy();
  });
});
