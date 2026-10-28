import Lenis from 'lenis';
import 'lenis/dist/lenis.css';

/**
 * Smooth scrolling with Lenis, in-page anchors that glide instead of jump, and a slow
 * drift on the hero as it leaves. Returns null when the visitor asks for reduced motion,
 * so the page falls back to native scrolling.
 */
export function setupScroll(): Lenis | null {
  if (matchMedia('(prefers-reduced-motion: reduce)').matches) return null;

  const lenis = new Lenis({ autoRaf: true, lerp: 0.09 });

  // Lenis reads scroll-padding-top and scroll-margin-top, so CSS keeps owning the header offset.
  // The skip link stays native so focus moves with it.
  document.addEventListener('click', (event) => {
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    const link = event.target instanceof Element ? event.target.closest<HTMLAnchorElement>('a[href^="#"]') : null;
    if (!link || link.classList.contains('skip-link')) return;
    const id = decodeURIComponent(link.hash.slice(1));
    const target = id ? document.getElementById(id) : null;
    if (id && !target) return;
    event.preventDefault();
    lenis.scrollTo(target ?? 0, { duration: 1.4 });
    history.replaceState(null, '', id ? `#${id}` : location.pathname + location.search);
  });

  // The hero trails the page slightly and fades as the preview takes over.
  const hero = document.querySelector<HTMLElement>('.hero-grid');
  if (hero) {
    lenis.on('scroll', ({ scroll }: Lenis) => {
      const progress = Math.min(1, Math.max(0, scroll / innerHeight));
      hero.style.setProperty('--drift', `${(progress * 64).toFixed(1)}px`);
      hero.style.setProperty('--fade', (1 - progress * 0.7).toFixed(3));
    });
  }

  return lenis;
}
