export function NoToken({ expired }: { expired?: boolean }) {
  return (
    <div className="notoken">
      <div className="notoken-card">
        <svg viewBox="0 0 20 20" width="40" height="40" aria-hidden="true">
          <rect x="1" y="1" width="18" height="18" rx="5" fill="var(--accent)" />
          <path d="M6 13.5V6.5h4a2.2 2.2 0 0 1 0 4.4H6m4 0 3 2.6" fill="none" stroke="#fff" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
        <h1>Open RepoManager from the tray icon</h1>
        <p>
          {expired
            ? 'This page’s session is no longer valid (RepoManager was probably restarted).'
            : 'This page needs the session token that the RepoManager app passes when it opens its window.'}{' '}
          Click the RepoManager icon in the Windows notification area, or run <code>devm status</code> in a terminal to start it.
        </p>
      </div>
    </div>
  );
}
