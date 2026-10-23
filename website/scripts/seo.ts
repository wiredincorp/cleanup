import type { Plugin } from 'vite';
import { readConfig } from '../src/config.ts';
import release from '../../release.json' with { type: 'json' };

const escape = (value: string) => value.replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]!);

export function seo(): Plugin {
  const config = readConfig(process.env, release);
  const origin = config.siteUrl ? new URL(config.siteUrl).origin : null;
  const indexable = origin !== null && process.env.VERCEL_ENV !== 'preview';
  return {
    name: 'cleanup-seo',
    transformIndexHtml: {
      order: 'post',
      handler(html, context) {
        const docs = context.path.includes('build-notes');
        const title = docs ? 'Download help | cleanup' : 'cleanup for Windows | A little less clutter';
        const description = docs
          ? 'What to expect when installing cleanup or running its portable Windows 11 version.'
          : 'A small Windows 11 app for reviewing app removals and organizing folders. Preview changes before anything moves.';
        const imageAlt = 'cleanup for Windows 11. A little less clutter. A lot more room.';
        const tags = [
          `<meta name="robots" content="${indexable ? 'index, follow' : 'noindex, nofollow'}" />`,
          `<meta name="twitter:title" content="${escape(title)}" />`,
          `<meta name="twitter:description" content="${escape(description)}" />`,
          `<meta name="twitter:image:alt" content="${escape(imageAlt)}" />`,
          '<meta property="og:site_name" content="cleanup" />',
          '<meta property="og:locale" content="en_US" />',
        ];
        if (origin) {
          const url = origin + (docs ? '/build-notes.html' : '/');
          const image = origin + '/social-v3.png';
          tags.push(`<link rel="canonical" href="${url}" />`, `<meta property="og:url" content="${url}" />`, `<meta name="twitter:image" content="${image}" />`);
          html = html.replace(/<meta property="og:image"[^>]*\/>/, `<meta property="og:image" content="${image}" />`);
          if (docs) tags.push(`<meta property="og:title" content="${escape(title)}" />`, `<meta property="og:description" content="${escape(description)}" />`, `<meta property="og:image" content="${image}" />`, `<meta property="og:image:alt" content="${escape(imageAlt)}" />`, '<meta name="twitter:card" content="summary_large_image" />');
          if (!docs) tags.push(`<script type="application/ld+json">${JSON.stringify({
            '@context': 'https://schema.org', '@type': 'SoftwareApplication', name: 'cleanup', url,
            operatingSystem: 'Windows 11', applicationCategory: 'UtilitiesApplication', description,
            downloadUrl: config.portableUrl,
            license: 'https://opensource.org/license/mit', codeRepository: config.repositoryUrl,
            publisher: { '@type': 'Organization', name: '(wired-in) community' },
          }).replace(/</g, '\\u003c')}</script>`);
        }
        return html.replace('</head>', `${tags.join('\n    ')}\n  </head>`);
      },
    },
    generateBundle() {
      this.emitFile({ type: 'asset', fileName: 'robots.txt', source: indexable ? `User-agent: *\nAllow: /\nSitemap: ${origin}/sitemap.xml\n` : 'User-agent: *\nDisallow: /\n' });
      if (origin) this.emitFile({ type: 'asset', fileName: 'sitemap.xml', source: `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>${escape(origin)}/</loc></url><url><loc>${escape(origin)}/build-notes.html</loc></url></urlset>\n` });
    },
  };
}
