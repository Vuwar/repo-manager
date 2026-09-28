import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it } from 'vitest';
import type { FolderDto } from '../api/types';
import { filterFolders, treeRows } from '../lib/folderTree';
import { FolderPicker } from './FolderPicker';

const folders: FolderDto[] = [
  { path: '.', markers: ['sln'] },
  { path: 'docs', markers: [] },
  { path: 'docs/img', markers: [] },
  { path: 'src', markers: [] },
  { path: 'src/Api', markers: ['csproj'] },
  { path: 'web', markers: ['package.json'] },
];

function Harness() {
  const [v, setV] = useState('');
  return <FolderPicker value={v} folders={folders} onChange={setV} />;
}

const items = () => screen.getAllByRole('treeitem').map((li) => li.querySelector('.folder-name')?.textContent);

describe('FolderPicker', () => {
  it('puts project folders first and filters by the typed text', () => {
    expect(filterFolders(folders, '').map((f) => f.path)).toEqual(['.', 'src/Api', 'web', 'docs', 'docs/img', 'src']);
    expect(filterFolders(folders, '.\\SRC\\').map((f) => f.path)).toEqual(['src/Api', 'src']);
  });

  it('builds tree rows from expanded folders only', () => {
    expect(treeRows(folders, new Set()).map((r) => [r.folder.path, r.depth, r.hasChildren])).toEqual([
      ['.', 0, false],
      ['docs', 0, true],
      ['src', 0, true],
      ['web', 0, false],
    ]);
    expect(treeRows(folders, new Set(['src'])).map((r) => r.folder.path)).toEqual(['.', 'docs', 'src', 'src/Api', 'web']);
  });

  it('opens as a tree with the way to project folders expanded', async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await user.click(screen.getByRole('combobox', { name: 'Folder' }));
    expect(items()).toEqual(['. (repo root)', 'docs', 'src', 'Api', 'web']);

    // Down to "docs", expand it with ArrowRight, collapse again with ArrowLeft.
    await user.keyboard('{ArrowDown}{ArrowRight}');
    expect(items()).toEqual(['. (repo root)', 'docs', 'img', 'src', 'Api', 'web']);
    await user.keyboard('{ArrowLeft}');
    expect(items()).toEqual(['. (repo root)', 'docs', 'src', 'Api', 'web']);

    await user.click(screen.getByRole('treeitem', { name: /Api/ }));
    expect((screen.getByRole('combobox') as HTMLInputElement).value).toBe('src/Api');
    expect(screen.queryByRole('tree')).toBeNull();
  });

  it('switches to a flat search while typing', async () => {
    const user = userEvent.setup();
    render(<Harness />);
    const input = screen.getByRole('combobox', { name: 'Folder' });
    await user.type(input, 'api');
    expect(screen.getAllByRole('option').map((o) => o.querySelector('.folder-name')?.textContent)).toEqual(['src/Api']);
    await user.keyboard('{Enter}');
    expect((input as HTMLInputElement).value).toBe('src/Api');
  });

  it('warns about a folder that is not in the repo', async () => {
    const user = userEvent.setup();
    render(<Harness />);
    await user.type(screen.getByRole('combobox', { name: 'Folder' }), 'nope');
    expect(screen.getByText(/does not exist/)).toBeTruthy();
  });
});
