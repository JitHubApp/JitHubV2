import React from 'react';

const store = 'https://apps.microsoft.com/store/detail/jithub/9MXRBJBB552V';
const source = 'https://github.com/JitHubApp/JitHubV2';
const media = {
  home: ['home-workspace', 'JitHub’s customizable Home workspace with global search, navigation, repository rail, overview, and activity widgets.', 'Recent work, repositories, and account activity in one Home view.'],
  pull: ['pull-request-conversation', 'A JitHub pull request conversation with navigation, Markdown, reactions, comments, and review actions.', 'A pull request with its discussion and review context together.'],
  editor: ['code-editor', 'JitHub’s repository code workspace with file navigation, breadcrumbs, branches, and a native source editor.', 'Repository navigation and source editing share the same workspace.'],
  csv: ['csv-table', 'A CSV file rendered as a sortable, virtualized native data table in JitHub.', 'CSV and TSV files open as sortable tables, with the original text still available.'],
  commit: ['commit-diff', 'JitHub’s commit workspace with history, changed-file tree, virtualized diff, comments, checks, and compare tools.', 'Changed files, the diff, checks, and discussion stay connected.'],
  stars: ['stars-library', 'The JitHub Stars library with smart lists, colored categories, search, sorting, and repository actions.', 'Stars can be searched, sorted, and grouped into categories.'],
  gists: ['gists-editor', 'JitHub’s Gists workspace with a searchable library, file detail, Markdown, and editing actions.', 'Browse a Gist, inspect its files, and edit it without leaving JitHub.'],
  profile: ['profile-overview', 'A JitHub profile overview with identity details, contribution history, and pinned repositories.', 'Identity, contribution history, and pinned repositories in one profile.']
};

const chapters = [
  {
    id: 'home', eyebrow: 'Home and workspace', title: 'Start with what needs your attention.',
    description: 'Home keeps recent activity, repositories, account overview, and common actions in one place. Choose the widgets that are useful to you and leave the rest out.',
    highlights: ['Search repositories and commands from anywhere in the app.', 'Arrange activity, recent repositories, and account overview around the way you work.', 'Keep frequently used repositories nearby in the collapsible navigation rail.']
  },
  {
    id: 'collaboration', eyebrow: 'Issues and pull requests', title: 'Read the thread. Review the change.',
    description: 'Issues and pull requests keep the conversation, files, commits, reviews, and status together. Personal queues and repository workspaces follow the same familiar shape.',
    highlights: ['Move between conversation, files, commits, and reviews without rebuilding context.', 'Reply, react, quote, edit, pin, hide, and copy links or Markdown from comments.', 'Review and merge with checks and repository status close by.'],
    featured: media.pull
  },
  {
    id: 'code', eyebrow: 'Code and rich files', title: 'Browse a repository without losing context.',
    description: 'Move through branches and files, read Markdown, inspect CSV or SVG content, and edit source from the same repository view.',
    highlights: ['Browse branches and file trees with breadcrumbs that keep the current path clear.', 'Read Markdown, SVG, CSV, and TSV files in views made for their content.', 'Use the native editor or switch back to the original source whenever you need it.']
  },
  {
    id: 'commits', eyebrow: 'Commits and changes', title: 'See what changed, file by file.',
    description: 'Commit history leads into a focused diff with changed files, checks, and discussion nearby. Branch comparison and search are available when the change is larger.',
    highlights: ['Scan history with filters available when the list needs narrowing.', 'Use the changed-file tree to move through or collapse parts of a larger diff.', 'Search and copy changes, inspect checks, compare branches, and discuss a commit.'],
    featured: media.commit
  },
  {
    id: 'library', eyebrow: 'Libraries and identity', title: 'Keep Stars, Gists, and people close.',
    description: 'Organize saved repositories, work with Gists, check profiles and contributions, triage notifications, and manage the account behind it all.',
    highlights: ['Group Stars with smart lists and colored categories, with search and bulk actions when needed.', 'Create and edit multi-file Gists alongside their rendered Markdown.', 'Move through profiles, notifications, repositories, and settings without leaving the app.']
  }
];

const capabilityGroups = [
  ['foundation', 'Native foundation', [
    ['Sign in', 'Device authorization, retry, cancellation, and private credential storage.'],
    ['Shell', 'Persistent navigation, repository rail, history, search, and title-bar actions.'],
    ['Home', 'Recent work, customizable widgets, repository navigation, and account overview.'],
    ['Notifications', 'Unread, read, done, subscription controls, badges, and internal routing.'],
    ['Settings', 'Live color themes, telemetry, cache, diagnostics, export, clearing, and About.'],
    ['Dialogs and flyouts', 'Predictable sizing, focus restoration, validation, and light dismiss.']
  ]],
  ['collaboration-capabilities', 'Collaboration', [
    ['My Issues', 'State, scope, filters, search, detail, editing, comments, and reactions.'],
    ['My Pull Requests', 'Personal review queues, sections, replies, reactions, review, and merge.'],
    ['Repository Issues', 'Issue lists, filters, details, metadata, state, and comments.'],
    ['Repository Pull Requests', 'Conversation, files, commits, reviews, timeline, and merge workflows.']
  ]],
  ['code-capabilities', 'Code and history', [
    ['Repository workspace', 'Identity, branches, actions, tabs, and repository controls.'],
    ['Repository code', 'Tree, breadcrumbs, branches, previews, find, outline, and large files.'],
    ['Repository commits', 'History, file trees, diffs, compare, search, copy, comments, and checks.'],
    ['Repository search', 'Typed results, filters, sorting, paging, and internal navigation.'],
    ['Native Markdown', 'Tables, tasks, code, images, SVG, links, selection, and copy.'],
    ['CSV and TSV', 'Frozen headers, virtualization, sorting, resizing, selection, copy, and UIA.'],
    ['SVG viewport', 'Secure rendering, cancellation, tiling, DPI support, and 0.1x to 8x zoom.'],
    ['Code editor', 'Highlighting, line numbers, find, go-to-line, wrapping, selection, and copy.']
  ]],
  ['library-capabilities', 'Libraries and account', [
    ['Stars', 'Smart lists, categories, colors, search, bulk actions, sync, and offline use.'],
    ['Gists', 'Paged library, file detail, create, edit, delete, and Markdown.'],
    ['Profiles', 'Authenticated and public profiles, lazy sections, edit, follow, and routing.'],
    ['Repositories', 'Filters, search, sorting, paging, creation, deletion, and offline state.']
  ]]
];

function ThemeImage({ item, className, eager = false }) {
  const [id, alt] = item;
  const light = `/media/showcase/${id}-light.png`;
  const dark = `/media/showcase/${id}-dark.png`;
  return <img className={className} src={light} data-theme-media="" data-theme-immediate={eager ? 'true' : undefined}
      data-theme-light-src={light} data-theme-dark-src={dark} alt={alt} width="3200" height="1800"
      loading={eager ? 'eager' : 'lazy'} decoding="async" fetchPriority={eager ? 'high' : 'auto'} />;
}

function PalettePreview({ palette, mode }) {
  const colors = palette[mode];
  const style = {
    '--theme-canvas': colors.canvas, '--theme-rail': colors.rail, '--theme-surface': colors.surface,
    '--theme-accent': colors.accent, '--theme-ink': colors.ink
  };
  return <div className="theme-preview">
    <span className="theme-preview__label">{mode === 'light' ? 'Light' : 'Dark'}</span>
    <div className="theme-swatch" role="img" aria-label={`${palette.name} ${mode} color preview`} style={style}>
      <span className="theme-swatch__rail" aria-hidden="true" />
      <span className="theme-swatch__surface" aria-hidden="true"><span className="theme-swatch__accent" /><span className="theme-swatch__ink" /></span>
    </div>
  </div>;
}

export default function App({ palettes }) {
  const featured = ['JitHub', 'Windows11', 'OneDarkPro', 'Dracula'].map(id => palettes.find(p => p.id === id));
  const additional = palettes.filter(p => !featured.includes(p));
  return <div className="site-shell">
    <header className="site-header">
      <a className="brand-lockup" href="/" aria-label="JitHub home">
        <img className="brand-lockup__mark" src="/JitHubLogo.png" alt="" />
        <span className="brand-lockup__copy"><span className="brand-lockup__wordmark">JitHub</span><span className="brand-lockup__subline">GitHub, native to Windows</span></span>
      </a>
      <nav className="site-header__actions" aria-label="Primary">
        <button className="theme-toggle" type="button" data-theme-toggle="" aria-label="Switch color theme" aria-pressed="false">
          <span className="theme-toggle__icon theme-toggle__icon--light" aria-hidden="true" />
          <span className="theme-toggle__icon theme-toggle__icon--dark" aria-hidden="true" />
          <span className="theme-toggle__label" data-theme-toggle-label="">Theme</span>
        </button>
        <a className="button button--ghost button--compact" href={source} target="_blank" rel="noopener noreferrer">Source</a>
        <a className="button button--primary button--compact" href={store} target="_blank" rel="noopener noreferrer">Get JitHub</a>
      </nav>
    </header>
    <main className="site-main">
      <section className="landing-grid">
        <section className="product-hero" id="intro" aria-labelledby="hero-title">
          <ThemeImage item={media.home} className="product-hero__image" eager />
          <div className="product-hero__copy">
            <p className="eyebrow eyebrow--hero">GitHub, native to Windows</p>
            <h1 id="hero-title">JitHub</h1>
            <p className="product-hero__lede">A native Windows app for repositories, issues, pull requests, code, and the everyday work around them.</p>
            <div className="button-row button-row--hero">
              <a className="button button--primary" href={store} target="_blank" rel="noopener noreferrer">Get JitHub</a>
              <a className="button button--hero-ghost" href={source} target="_blank" rel="noopener noreferrer">View source</a>
            </div>
          </div>
          <p className="product-hero__caption">{media.home[2]}</p>
        </section>
        <aside className="page-rail surface-card" aria-label="Landing page navigation">
          <div className="page-rail__block"><p className="page-rail__eyebrow">On this page</p>
            <nav className="page-rail__nav" aria-label="Page sections">
              <a href="#intro">JitHub</a>
              {chapters.map(chapter => <a href={`#${chapter.id}`} key={chapter.id}>{chapter.eyebrow}</a>)}
              <a href="#themes">Color themes</a><a href="#capabilities">Everything included</a>
            </nav>
          </div>
          <div className="page-rail__block page-rail__proof"><p className="page-rail__eyebrow">Built for Windows</p>
            <p className="page-rail__note">Native AOT across x86, x64, and ARM64, with cached-first loading and accessible controls throughout.</p>
          </div>
        </aside>
        <div className="page-content">
          <section className="trust-strip" aria-label="JitHub engineering qualities">
            <div className="trust-strip__intro"><p className="eyebrow">Built for Windows</p><p>Native where it counts, useful when the connection changes.</p></div>
            <ul className="trust-strip__list"><li>Native AOT <span>x86, x64, ARM64</span></li><li>Cached first <span>Offline-aware</span></li><li>Accessible <span>Keyboard and High Contrast</span></li><li>Private diagnostics <span>No account identifiers</span></li></ul>
          </section>
          <div className="chapter-list">
            {chapters.map(chapter => <section className={`showcase-chapter ${chapter.featured ? 'showcase-chapter--visual' : 'showcase-chapter--text'}`}
              id={chapter.id} aria-labelledby={`${chapter.id}-title`} key={chapter.id}>
              <div className="showcase-chapter__copy"><p className="eyebrow">{chapter.eyebrow}</p><h2 id={`${chapter.id}-title`}>{chapter.title}</h2><p className="showcase-chapter__description">{chapter.description}</p></div>
              <ul className="showcase-chapter__highlights">{chapter.highlights.map(value => <li key={value}>{value}</li>)}</ul>
              {chapter.featured && <figure className={`product-frame product-frame--${chapter.featured[0]}`}>
                <ThemeImage item={chapter.featured} className="product-frame__image" /><figcaption>{chapter.featured[2]}</figcaption>
              </figure>}
            </section>)}
          </div>
          <section className="theme-library" id="themes" aria-labelledby="themes-title">
            <header className="theme-library__heading"><p className="eyebrow">Color themes</p><h2 id="themes-title">Choose a color theme in Settings.</h2>
              <p>Twenty color families are included. Each supports Light and Dark and applies immediately. High Contrast, transparency, and reduced motion continue to follow Windows.</p>
            </header>
            <div className="theme-library__list">
              {featured.map(palette => <article className="theme-option" key={palette.id}>
                <div className="theme-option__copy"><h3>{palette.name}</h3><p>{palette.description}</p></div>
                <div className="theme-option__previews"><PalettePreview palette={palette} mode="light" /><PalettePreview palette={palette} mode="dark" /></div>
              </article>)}
              <ul className="theme-library__index" aria-label="More included color themes">
                {additional.map(palette => <li title={palette.description} key={palette.id}>
                  <span className="theme-chip" aria-hidden="true" style={{
                    '--theme-canvas': palette.dark.canvas, '--theme-rail': palette.dark.rail,
                    '--theme-surface': palette.dark.surface, '--theme-accent': palette.dark.accent,
                    '--theme-ink': palette.dark.ink
                  }}><span className="theme-chip__rail" /><span className="theme-chip__accent" /></span><span>{palette.name}</span>
                </li>)}
              </ul>
            </div>
          </section>
          <section className="capability-index" id="capabilities" aria-labelledby="capabilities-title">
            <header className="section-heading"><p className="eyebrow">What you can do</p><h2 id="capabilities-title">The main parts of GitHub, close at hand.</h2>
              <p>JitHub covers the everyday repository work as well as the account, library, and file tools around it.</p>
            </header>
            <div className="capability-groups">{capabilityGroups.map(([id, title, items]) =>
              <section className="capability-group" aria-labelledby={`${id}-title`} key={id}>
                <h3 id={`${id}-title`}>{title}</h3><div className="capability-group__items">{items.map(([name, description]) =>
                  <article className="capability-item" key={name}><h4>{name}</h4><p>{description}</p></article>)}</div>
              </section>)}</div>
          </section>
          <section className="closing-cta surface-card" aria-labelledby="closing-title">
            <div><p className="eyebrow">For Windows</p><h2 id="closing-title">Try JitHub on your desktop.</h2><p>Install JitHub from the Microsoft Store, or explore the complete open source project.</p></div>
            <div className="button-row"><a className="button button--primary" href={store} target="_blank" rel="noopener noreferrer">Open Microsoft Store</a>
              <a className="button button--ghost" href={source} target="_blank" rel="noopener noreferrer">Browse the repository</a></div>
          </section>
        </div>
      </section>
    </main>
    <footer className="site-footer">
      <div className="site-footer__meta"><p className="site-footer__eyebrow">JitHub</p><p className="site-footer__body">An open source GitHub client for Windows, built for repositories, pull requests, issues, and code.</p></div>
      <div className="site-footer__links"><a href={source} target="_blank" rel="noopener noreferrer">GitHub</a><a href={store} target="_blank" rel="noopener noreferrer">Microsoft Store</a></div>
    </footer>
  </div>;
}
