import { describe, expect, it } from 'vitest';
import { cleanEntry, formatPrepare, joinCommandLine, parseOverrides, parsePrepare, splitCommandLine, validateName } from './overrides';

describe('command line', () => {
  it('splits on spaces and keeps quoted parts together', () => {
    expect(splitCommandLine('npm run dev -- --port ${port:web}')).toEqual(['npm', 'run', 'dev', '--', '--port', '${port:web}']);
    expect(splitCommandLine('  cmd  "a b"  "" \\"x\\" ')).toEqual(['cmd', 'a b', '', '"x"']);
    expect(splitCommandLine('   ')).toEqual([]);
  });

  it('round-trips through join', () => {
    const line = joinCommandLine('dotnet', ['run', '--project', 'src/My App', '']);
    expect(line).toBe('dotnet run --project "src/My App" ""');
    expect(splitCommandLine(line)).toEqual(['dotnet', 'run', '--project', 'src/My App', '']);
  });
});

describe('prepare steps', () => {
  it('parses conditions and plain commands', () => {
    const steps = parsePrepare('if !exists(web/node_modules): npm ci --prefix web\n\n  dotnet restore ');
    expect(steps).toEqual([{ if: '!exists(web/node_modules)', run: 'npm ci --prefix web' }, { run: 'dotnet restore' }]);
    expect(formatPrepare(steps)).toBe('if !exists(web/node_modules): npm ci --prefix web\ndotnet restore');
  });
});

describe('overrides', () => {
  it('fills missing blocks and rejects wrong shapes', () => {
    expect(parseOverrides('')).toEqual({ services: {}, tasks: {} });
    expect(parseOverrides('{"services":{"a":{"port":1}}}')).toEqual({ services: { a: { port: 1 } }, tasks: {} });
    expect(parseOverrides('[]')).toBeNull();
    expect(parseOverrides('{"services":[]}')).toBeNull();
    expect(parseOverrides('{')).toBeNull();
  });

  it('drops empty values', () => {
    expect(cleanEntry({ command: 'npm', cwd: '', port: NaN, env: {}, url: undefined, autoRestart: false })).toEqual({ command: 'npm', autoRestart: false });
  });

  it('validates names', () => {
    expect(validateName('', [])).toBeTruthy();
    expect(validateName('my api', [])).toBeTruthy();
    expect(validateName('API', ['api'])).toBeTruthy();
    expect(validateName('web-2', ['api'])).toBeNull();
  });
});
