# cleanup website

The product site for cleanup. Bun, Vite 8, TypeScript, and Tailwind CSS 4, with no
framework. It is two static HTML pages with progressive TypeScript interactions.

```powershell
bun install
bun run dev        # http://localhost:5180
bun run check      # type-check browser code and Bun scripts separately
bun test           # config parsing and WinGet manifest generation
bun run build      # check + static output in dist/
```

`dist/` is plain static files. Host it anywhere: GitHub Pages, Cloudflare Pages, or any static file server.

## Release links

Public GitHub, Discord, and production-site links come from `../release.json`,
shared with the Windows app. Its download links point to a per-user setup EXE and a
single-file portable Cleanup.exe. Environment variables can override the defaults:

| Variable | Effect |
| --- | --- |
| `VITE_PORTABLE_URL` | Overrides the portable Cleanup.exe download. Must be `https://`. |
| `VITE_DOWNLOAD_URL` | Legacy alias for `VITE_PORTABLE_URL`. |
| `VITE_SETUP_URL` | Overrides the per-user Windows setup download. Must be `https://`. |
| `VITE_REPOSITORY_URL` | Shows the source links in the footer and the open-source section. |
| `VITE_WINGET_ID` | Shows `winget install --id <id> --exact --source winget` with a copy button. |
| `VITE_DISCORD_URL` | Overrides the Discord support invite. |
| `VITE_SITE_URL` | Sets the canonical production origin for SEO and sitemap generation. |
| `VITE_APP_INSTALLER_URL` | Enables installation through the signed, Windows-managed update feed. |

Invalid values, such as non-HTTPS URLs or malformed package IDs, are ignored rather
than rendered. Every `VITE_` value ends up in public JavaScript, so never put secrets in them.

## Optional MSIX WinGet tooling

The existing generator targets MSIX only. It does not generate manifests for the
new EXE installer; adapt it and validate the Inno installer type before submitting
the desktop preview to WinGet.

`bun run winget:prepare` writes the version, locale, and installer manifests for an MSIX
release. It uses manifest schema 1.10.0 and the `manifests/<letter>/<Publisher>/<Package>/<version>/` layout:

```powershell
bun run winget:prepare --id Publisher.Cleanup --version 1.0.0 `
  --url https://example.com/cleanup-1.0.0-x64.msix `
  --file ..\artifacts\release\cleanup-1.0.0-x64.msix `
  --publisher "cleanup contributors" `
  --package-family-name <PackageFamilyName> `
  --dependency <WindowsAppRuntimePackageId>@<MinVersion>

winget validate --manifest winget-output\manifests\p\Publisher\Cleanup\1.0.0
```

`--file` hashes the exact MSIX you published. You can pass `--sha256` instead, and if
you pass both, they must match. The script only writes files under `winget-output/`. It
never submits anything. To publish, test with `winget install --manifest <folder>` on a
clean machine, then open a pull request against
[microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs). Once that pull request
merges, set `VITE_WINGET_ID`.

Before the first submission, you need a production-signed MSIX, a stable public download
URL, and the Windows App SDK runtime dependency declared with `--dependency`. See
[release preparation](../docs/release-preparation.md).
