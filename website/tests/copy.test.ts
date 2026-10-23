import { expect, test } from 'bun:test';
import { findCopyIssues } from '../scripts/check-copy.ts';

test('website copy and SEO metadata exclude prohibited dash characters', () => {
  expect(findCopyIssues()).toEqual([]);
});
