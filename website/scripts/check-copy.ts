import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { extname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const websiteRoot = fileURLToPath(new URL('../', import.meta.url));
const textExtensions = new Set(['.css', '.html', '.js', '.json', '.svg', '.ts', '.txt', '.xml']);

function textFiles(path: string): string[] {
  if (!existsSync(path)) return [];
  const stat = statSync(path);
  if (stat.isDirectory()) return readdirSync(path).flatMap((name) => textFiles(join(path, name)));
  return textExtensions.has(extname(path).toLowerCase()) ? [path] : [];
}

export function findCopyIssues(includeDist = false): string[] {
  const roots = [
    join(websiteRoot, 'index.html'),
    join(websiteRoot, 'build-notes.html'),
    join(websiteRoot, 'src'),
    join(websiteRoot, 'scripts', 'seo.ts'),
    join(websiteRoot, 'public'),
    join(websiteRoot, '..', 'release.json'),
  ];
  if (includeDist) roots.push(join(websiteRoot, 'dist'));

  const files = [...new Set(roots.flatMap(textFiles))];
  const issues: string[] = [];
  for (const file of files) {
    const contents = readFileSync(file, 'utf8');
    const prohibited = /[\u2013\u2014]/gu;
    for (const match of contents.matchAll(prohibited)) {
      const line = contents.slice(0, match.index).split('\n').length;
      const codePoint = match[0] === '\u2013' ? 'U+2013' : 'U+2014';
      issues.push(`${relative(websiteRoot, file)}:${line}: prohibited ${codePoint}`);
    }
  }
  return issues;
}

if (import.meta.main) {
  const issues = findCopyIssues(process.argv.includes('--dist'));
  if (issues.length) {
    console.error(issues.join('\n'));
    process.exitCode = 1;
  } else {
    console.log(`Checked website copy and SEO in ${process.argv.includes('--dist') ? 'source and dist' : 'source'}.`);
  }
}
