// Public release links. Each value stays null until it is real, so the page
// never advertises a download, repository, or package that doesn't exist.

export type SiteConfig = {
  repositoryUrl: string | null;
  portableUrl: string | null;
  wingetId: string | null;
  discordUrl: string | null;
  siteUrl: string | null;
  appInstallerUrl: string | null;
  setupUrl: string | null;
};

type Env = Readonly<Record<string, string | undefined>>;
type Defaults = Partial<Record<keyof SiteConfig, string>> & { downloadUrl?: string };

// WinGet identifiers are Publisher.Package with optional extra segments.
const WINGET_ID = /^[^.\s\\/:*?"<>|]{1,32}(\.[^.\s\\/:*?"<>|]{1,32}){1,7}$/;

export function httpsUrl(value: string | undefined): string | null {
  const trimmed = value?.trim();
  if (!trimmed) return null;
  try {
    const url = new URL(trimmed);
    return url.protocol === 'https:' && !url.username && !url.password ? url.href : null;
  } catch {
    return null;
  }
}

export function wingetId(value: string | undefined): string | null {
  const trimmed = value?.trim();
  return trimmed && WINGET_ID.test(trimmed) ? trimmed : null;
}

export function readConfig(env: Env, defaults: Defaults = {}): SiteConfig {
  return {
    repositoryUrl: httpsUrl(env.VITE_REPOSITORY_URL ?? defaults.repositoryUrl),
    portableUrl: httpsUrl(env.VITE_PORTABLE_URL ?? env.VITE_DOWNLOAD_URL ?? defaults.portableUrl ?? defaults.downloadUrl),
    wingetId: wingetId(env.VITE_WINGET_ID),
    discordUrl: discordInvite(env.VITE_DISCORD_URL ?? defaults.discordUrl),
    siteUrl: httpsUrl(env.VITE_SITE_URL ?? defaults.siteUrl),
    appInstallerUrl: httpsUrl(env.VITE_APP_INSTALLER_URL ?? defaults.appInstallerUrl),
    setupUrl: httpsUrl(env.VITE_SETUP_URL ?? defaults.setupUrl),
  };
}

export function discordInvite(value: string | undefined): string | null {
  const safe = httpsUrl(value);
  if (!safe) return null;
  const url = new URL(safe);
  return ((url.hostname === 'discord.gg' && /^\/[a-zA-Z0-9-]+\/?$/.test(url.pathname)) ||
    (url.hostname === 'discord.com' && /^\/invite\/[a-zA-Z0-9-]+\/?$/.test(url.pathname))) && !url.search && !url.hash ? url.href : null;
}

export function latestReleaseUrl(repository: string | null): string | null {
  if (!repository) return null;
  const url = new URL(repository);
  return url.hostname === 'github.com' && /^\/[\w.-]+\/[\w.-]+\/?$/.test(url.pathname)
    ? `${url.origin}${url.pathname.replace(/\/$/, '').replace(/\.git$/, '')}/releases/latest` : null;
}

export function releasePageUrl(config: SiteConfig): string | null {
  if (config.portableUrl) {
    const url = new URL(config.portableUrl);
    const match = url.pathname.match(/^(\/[^/]+\/[^/]+\/releases)\/download\/([^/]+)\/[^/]+$/);
    if (url.hostname === 'github.com' && match) return `${url.origin}${match[1]}/tag/${match[2]}`;
  }
  const latest = latestReleaseUrl(config.repositoryUrl);
  return config.appInstallerUrl ? latest : latest?.replace(/\/latest$/, '') ?? null;
}

export const wingetCommand = (id: string) => `winget install --id ${id} --exact --source winget`;
