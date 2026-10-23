import { describe, expect, test } from 'bun:test';
import { discordInvite, latestReleaseUrl, readConfig, releasePageUrl, wingetCommand } from '../src/config.ts';

describe('readConfig', () => {
  test('everything stays unset by default', () => {
    expect(readConfig({})).toEqual({ repositoryUrl: null, portableUrl: null, wingetId: null, discordUrl: null, siteUrl: null, appInstallerUrl: null, setupUrl: null });
    expect(readConfig({ VITE_REPOSITORY_URL: '  ', VITE_DOWNLOAD_URL: '', VITE_WINGET_ID: '' }).portableUrl).toBeNull();
  });

  test('accepts https links and real-looking package ids', () => {
    const config = readConfig({
      VITE_REPOSITORY_URL: 'https://github.com/example/cleanup',
      VITE_DOWNLOAD_URL: 'https://example.com/cleanup.exe',
      VITE_WINGET_ID: 'Example.Cleanup',
    });
    expect(config).toEqual({
      repositoryUrl: 'https://github.com/example/cleanup',
      portableUrl: 'https://example.com/cleanup.exe',
      wingetId: 'Example.Cleanup',
      discordUrl: null, siteUrl: null, appInstallerUrl: null, setupUrl: null,
    });
  });

  test('rejects insecure or unsafe values', () => {
    const config = readConfig({
      VITE_REPOSITORY_URL: 'http://example.com',
      VITE_DOWNLOAD_URL: 'javascript:alert(1)',
      VITE_WINGET_ID: 'cleanup; rm -rf',
    });
    expect(config).toEqual({ repositoryUrl: null, portableUrl: null, wingetId: null, discordUrl: null, siteUrl: null, appInstallerUrl: null, setupUrl: null });
    expect(readConfig({ VITE_WINGET_ID: 'NoDot' }).wingetId).toBeNull();
  });
});

test('prerelease downloads link to their tag instead of the stable-only latest route', () => {
  const config = readConfig({ VITE_REPOSITORY_URL: 'https://github.com/wiredincorp/cleanup', VITE_DOWNLOAD_URL: 'https://github.com/wiredincorp/cleanup/releases/download/v0.1.1-rc.1/Cleanup.exe' });
  expect(releasePageUrl(config)).toBe('https://github.com/wiredincorp/cleanup/releases/tag/v0.1.1-rc.1');
  expect(releasePageUrl(readConfig({ VITE_REPOSITORY_URL: 'https://github.com/wiredincorp/cleanup' }))).toBe('https://github.com/wiredincorp/cleanup/releases');
});

test('release links use the GitHub latest release and reject unrelated paths', () => {
  expect(latestReleaseUrl('https://github.com/wiredincorp/cleanup/')).toBe('https://github.com/wiredincorp/cleanup/releases/latest');
  expect(latestReleaseUrl('https://example.com/wiredincorp/cleanup')).toBeNull();
  expect(latestReleaseUrl('https://github.com/wiredincorp/cleanup/issues')).toBeNull();
});

test('VITE_PORTABLE_URL takes priority while the existing download override remains supported', () => {
  expect(readConfig({
    VITE_PORTABLE_URL: 'https://example.com/portable.exe',
    VITE_DOWNLOAD_URL: 'https://example.com/legacy.exe',
  }).portableUrl).toBe('https://example.com/portable.exe');
  expect(readConfig({ VITE_DOWNLOAD_URL: 'https://example.com/legacy.exe' }).portableUrl).toBe('https://example.com/legacy.exe');
  expect(readConfig({}, { downloadUrl: 'https://example.com/default.exe' }).portableUrl).toBe('https://example.com/default.exe');
});

test('Discord links are invites and environment overrides public defaults', () => {
  expect(discordInvite('https://discord.gg/5kRtCcNWU6')).toBe('https://discord.gg/5kRtCcNWU6');
  expect(discordInvite('https://discord.gg.evil.test/5kRtCcNWU6')).toBeNull();
  expect(discordInvite('https://user:password@discord.gg/5kRtCcNWU6')).toBeNull();
  expect(readConfig({ VITE_DISCORD_URL: '' }, { discordUrl: 'https://discord.gg/5kRtCcNWU6' }).discordUrl).toBeNull();
});

test('wingetCommand pins the exact id and community source', () => {
  expect(wingetCommand('Example.Cleanup')).toBe('winget install --id Example.Cleanup --exact --source winget');
});
