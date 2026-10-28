const SVG_NS = 'http://www.w3.org/2000/svg';

/** Enhance the existing marks and reveal each piece of content once, as it enters. */
export function setupMotion() {
  // Keep the original image as the no-JavaScript fallback. Inline tiles let the
  // existing mark assemble independently without changing its geometry.
  for (const image of document.querySelectorAll<HTMLImageElement>('.brand img')) {
    const mark = document.createElementNS(SVG_NS, 'svg');
    mark.setAttribute('viewBox', '0 0 638 638');
    mark.setAttribute('width', image.getAttribute('width') ?? '26');
    mark.setAttribute('height', image.getAttribute('height') ?? '26');
    mark.setAttribute('aria-hidden', 'true');
    mark.classList.add('logo-mark');
    if (image.className) mark.classList.add(...image.className.split(/\s+/).filter(Boolean));
    for (const [index, [x, y]] of [[0, 0], [351.625, 0], [0, 351.625], [351.625, 351.625]].entries()) {
      const tile = document.createElementNS(SVG_NS, 'rect');
      tile.setAttribute('x', String(x));
      tile.setAttribute('y', String(y));
      tile.setAttribute('width', '286.375');
      tile.setAttribute('height', '286.375');
      tile.setAttribute('rx', '68.875');
      tile.classList.add('logo-tile');
      tile.style.setProperty('--tile-delay', `${index * 65}ms`);
      mark.append(tile);
    }
    image.replaceWith(mark);
  }

  const preference = matchMedia('(prefers-reduced-motion: reduce)');
  for (const brand of document.querySelectorAll<HTMLAnchorElement>('.site-header .brand')) {
    const replay = () => {
      if (preference.matches) return;
      brand.querySelectorAll<SVGRectElement>('.logo-tile').forEach((tile, index) => {
        for (const animation of tile.getAnimations()) animation.cancel();
        const style = getComputedStyle(tile);
        tile.animate([
          { transform: `translate(${style.getPropertyValue('--tile-x')}, ${style.getPropertyValue('--tile-y')}) rotate(${style.getPropertyValue('--tile-turn')}) scale(.78)` },
          { transform: 'translate(0, 0) rotate(0) scale(1)' },
        ], { duration: 560, delay: index * 40, easing: 'cubic-bezier(.16, 1, .3, 1)', fill: 'backwards' });
      });
    };
    brand.addEventListener('pointerenter', replay);
    brand.addEventListener('focus', replay);
    brand.addEventListener('click', replay);
  }
  if (preference.matches || !('IntersectionObserver' in window) || document.documentElement.dataset.motion !== 'pending') {
    delete document.documentElement.dataset.motion;
    return;
  }

  const targets = new Set<Element>();
  const group = (selector: string, stagger = 0, delay = 0) => {
    document.querySelectorAll<HTMLElement | SVGSVGElement>(selector).forEach((element, index) => {
      element.setAttribute('data-reveal', '');
      element.style.setProperty('--reveal-delay', `${delay + index * stagger}ms`);
      targets.add(element);
    });
  };
  group('.logo-mark');
  group('.hero-label, .hero-title-line, .hero-description, .hero-actions, .hero-note', 90, 60);
  group('.product-stage', 0, 260);
  group('.section-head, .download-foot, .faq-list, .community, .footer-row');
  group('.feature-list li', 90);
  group('.download-choice', 90);
  group('.docs-main > *', 60);

  const show = (element: Element) => {
    element.classList.add('is-revealed');
    observer.unobserve(element);
  };
  const observer = new IntersectionObserver((entries) => {
    for (const entry of entries) if (entry.isIntersecting) show(entry.target);
  }, { threshold: 0.04, rootMargin: '0px 0px -24px 0px' });

  for (const element of targets) observer.observe(element);
  // Hand off the pre-paint state to the observer without a visible-then-hidden frame.
  document.documentElement.dataset.motion = 'ready';

  document.addEventListener('focusin', (event) => {
    if (!(event.target instanceof Element)) return;
    // Keyboard focus must never land in content still awaiting a scroll reveal.
    for (const element of targets) {
      if (element.contains(event.target)) {
        show(element);
        (element as HTMLElement | SVGSVGElement).style.setProperty('--reveal-delay', '0ms');
        (element as HTMLElement | SVGSVGElement).style.animation = 'none';
      }
    }
  });
  preference.addEventListener('change', () => {
    if (!preference.matches) return;
    observer.disconnect();
    delete document.documentElement.dataset.motion;
  });
}
