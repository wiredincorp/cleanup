import { readConfig, releasePageUrl } from './config.ts';
import release from '../../release.json';
import { setupMotion } from './motion.ts';

const config = readConfig(import.meta.env, release);
const $ = <T extends Element = HTMLElement>(selector: string, root: ParentNode = document) => root.querySelector<T>(selector);
const $$ = <T extends Element = HTMLElement>(selector: string, root: ParentNode = document) => [...root.querySelectorAll<T>(selector)];
const setText = (selector: string, text: string) => {
  const element = $(selector);
  if (element) element.textContent = text;
};

// Compact navigation uses the same destinations as the desktop header.
const menuToggle = $<HTMLButtonElement>('.menu-toggle');
const navigation = $('#main-navigation');
const headerSurface = $('.header-surface');
function setMenu(open: boolean) {
  menuToggle?.setAttribute('aria-expanded', String(open));
  menuToggle?.setAttribute('aria-label', open ? 'Close navigation' : 'Open navigation');
  menuToggle?.querySelector('use')?.setAttribute('href', open ? '#i-close' : '#i-menu');
  navigation?.classList.toggle('is-open', open);
  headerSurface?.classList.toggle('is-menu-open', open);
}
const closeMenu = () => setMenu(false);
menuToggle?.addEventListener('click', () => {
  const open = menuToggle.getAttribute('aria-expanded') !== 'true';
  setMenu(open);
  // The sheet sits before the toggle in source order, so bring focus into it.
  if (open) navigation?.querySelector('a')?.focus();
});
navigation?.addEventListener('click', (event) => {
  if (event.target instanceof Element && event.target.closest('a')) closeMenu();
});
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && menuToggle?.getAttribute('aria-expanded') === 'true') {
    closeMenu();
    menuToggle.focus();
  }
});
document.addEventListener('click', (event) => {
  if (event.target instanceof Node && !menuToggle?.contains(event.target) && !navigation?.contains(event.target)) closeMenu();
});
matchMedia('(min-width: 768px)').addEventListener('change', closeMenu);

// A hairline appears under the header once content scrolls beneath it.
const markScrolled = () => headerSurface?.classList.toggle('is-scrolled', scrollY > 4);
addEventListener('scroll', markScrolled, { passive: true });
markScrolled();

// Tabs: shared by the preview rail and the install panel (WAI-ARIA tabs pattern).
function setupTabs(tabs: HTMLButtonElement[]) {
  const select = (tab: HTMLButtonElement, focus = false) => {
    for (const other of tabs) {
      const active = other === tab;
      other.setAttribute('aria-selected', String(active));
      other.tabIndex = active ? 0 : -1;
      const panel = document.getElementById(other.getAttribute('aria-controls') ?? '');
      if (panel) panel.hidden = !active;
    }
    if (focus) tab.focus();
  };

  tabs.forEach((tab, index) => {
    tab.addEventListener('click', () => select(tab));
    tab.addEventListener('keydown', (event) => {
      const vertical = tab.parentElement?.getAttribute('aria-orientation') === 'vertical';
      const next = vertical ? 'ArrowDown' : 'ArrowRight';
      const previous = vertical ? 'ArrowUp' : 'ArrowLeft';
      const target = {
        [next]: tabs[(index + 1) % tabs.length],
        [previous]: tabs[(index - 1 + tabs.length) % tabs.length],
        Home: tabs[0],
        End: tabs.at(-1),
      }[event.key];
      if (!target) return;
      event.preventDefault();
      select(target, true);
    });
  });

  return select;
}

const selectView = setupTabs($$<HTMLButtonElement>('.app-tabs [role="tab"]'));
setupTabs($$<HTMLButtonElement>('.install-tabs [role="tab"]'));

// Keep keyboard navigation aligned with the preview's container-based layout.
const previewWindow = $('.app-window');
const previewTabs = $('.app-tabs');
if (previewWindow) {
  new ResizeObserver(([entry]) => {
    if (entry) previewTabs?.setAttribute('aria-orientation', entry.contentRect.width <= 520 ? 'horizontal' : 'vertical');
  }).observe(previewWindow);
}

for (const link of $$<HTMLAnchorElement>('[data-show-view]')) {
  link.addEventListener('click', () => {
    const tab = $<HTMLButtonElement>(`.app-tabs [data-view="${link.dataset.showView}"]`);
    if (tab) selectView(tab);
  });
}

// Example application list: search, selection, and the selection summary in the rail and the compact footer.
const rows = $$<HTMLLabelElement>('#application-list .app-row');
const search = $<HTMLInputElement>('#app-search');
const count = $('.app-count');
const empty = $('.empty-search');
const summaries = $$('[data-selection-summary]');
const reviewButtons = $$<HTMLButtonElement>('[data-review]');
const selectionActive = $('[data-selection-active]');
const selectionEmpty = $('[data-selection-empty]');
const selectionIcons = $('#selection-icons');
const dialog = $<HTMLDialogElement>('#selection-dialog');
const dialogList = $('#dialog-selection');

const checkedRows = () => rows.filter((row) => $<HTMLInputElement>('input', row)?.checked);
const selectedNames = () => checkedRows().map((row) => $<HTMLInputElement>('input', row)?.value ?? '');

// As in the app: up to six selected icons, then +N for the rest.
function renderSelection() {
  const selected = checkedRows();
  const total = selected.length;
  const summary = total === 0 ? 'You choose what goes.' : `${total} selected · Nothing removed yet.`;
  for (const element of summaries) element.textContent = summary;
  for (const button of reviewButtons) button.disabled = total === 0;
  if (selectionActive) selectionActive.hidden = total === 0;
  if (selectionEmpty) selectionEmpty.hidden = total > 0;
  selectionIcons?.replaceChildren(
    ...selected.slice(0, 6).map((row) => $('.sample-icon', row)!.cloneNode(true) as HTMLElement),
    ...(total > 6 ? [Object.assign(document.createElement('span'), { className: 'sel-more', textContent: `+${total - 6}` })] : []),
  );
}

search?.addEventListener('input', () => {
  const query = search.value.trim().toLowerCase();
  let visible = 0;
  for (const row of rows) {
    const match = !query || (row.dataset.name ?? '').includes(query);
    row.hidden = !match;
    if (match) visible++;
  }
  if (count) count.textContent = String(visible);
  if (empty) empty.hidden = visible > 0;
});

$('#application-list')?.addEventListener('change', renderSelection);

$('#clear-selection')?.addEventListener('click', () => {
  for (const row of rows) {
    const input = $<HTMLInputElement>('input', row);
    if (input) input.checked = false;
  }
  renderSelection();
});

for (const button of reviewButtons) {
  button.addEventListener('click', () => {
    if (!dialog || !dialogList) return;
    dialogList.replaceChildren(...selectedNames().map((name) => Object.assign(document.createElement('li'), { textContent: name })));
    dialog.showModal();
  });
}

renderSelection();

dialog?.addEventListener('click', (event) => {
  if (event.target === dialog) dialog.close();
});

// Organize: a finished move offers Undo in place.
const move = $<HTMLButtonElement>('#organize-move');
const result = $('#organize-result');
move?.addEventListener('click', () => {
  if (result) result.hidden = false;
  if (move) move.disabled = true;
});
$('#organize-undo')?.addEventListener('click', () => {
  if (result) result.hidden = true;
  if (move) move.disabled = false;
});

// Theme.
// The header has an icon-only toggle; the compact menu has a labelled one.
const themeToggles = $$<HTMLButtonElement>('.theme-toggle');
const themeColor = $<HTMLMetaElement>('meta[name="theme-color"]');
const systemTheme = matchMedia('(prefers-color-scheme: dark)');

function applyTheme(dark: boolean) {
  document.documentElement.dataset.theme = dark ? 'dark' : 'light';
  for (const toggle of themeToggles) {
    $('use', toggle)?.setAttribute('href', dark ? '#i-sun' : '#i-moon');
    const label = $('[data-theme-label]', toggle);
    if (label) label.textContent = dark ? 'Use light theme' : 'Use dark theme';
    else toggle.setAttribute('aria-label', dark ? 'Switch to light theme' : 'Switch to dark theme');
  }
  themeColor?.setAttribute('content', dark ? '#1b1b1b' : '#f7f8f7');
}

applyTheme(document.documentElement.dataset.theme === 'dark');
systemTheme.addEventListener('change', (event) => {
  try {
    if (!localStorage.getItem('cleanup-theme')) applyTheme(event.matches);
  } catch {
    applyTheme(event.matches);
  }
});
for (const toggle of themeToggles) {
  toggle.addEventListener('click', () => {
    const dark = document.documentElement.dataset.theme !== 'dark';
    applyTheme(dark);
    try { localStorage.setItem('cleanup-theme', dark ? 'dark' : 'light'); } catch { /* private mode */ }
  });
}

if (config.repositoryUrl) {
  for (const link of $$<HTMLAnchorElement>('[data-repository]')) {
    link.href = config.repositoryUrl;
    link.hidden = false;
  }
  setText('#source-faq', 'Yes. cleanup is MIT licensed and its source is public on the project repository.');
  setText('#source-description', 'Native C# and WinUI on the surface. Rust for the system work. MIT licensed, with the source on GitHub.');
}

for (const [selector, url] of [
  ['[data-discord]', config.discordUrl],
  ['[data-releases]', releasePageUrl(config)],
  ['[data-setup]', config.setupUrl],
  ['[data-download]', config.portableUrl],
] as const) {
  for (const link of $$<HTMLAnchorElement>(selector)) {
    if (url) {
      link.href = url;
      link.hidden = false;
    } else if (selector === '[data-setup]' || selector === '[data-download]') {
      link.hidden = true;
    }
  }
}

setupMotion();
