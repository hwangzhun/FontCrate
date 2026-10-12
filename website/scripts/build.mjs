import { mkdir, rm, cp, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import { content } from '../content.mjs';
import { render, escape } from '../template.mjs';

const root = fileURLToPath(new URL('../', import.meta.url));
export function configuration(env = process.env) {
  const httpsUrl = (value, name, originOnly = false) => {
    if (!value?.trim()) return '';
    let url;
    try { url = new URL(value.trim()); } catch { throw new Error(`${name} must be a valid HTTPS URL.`); }
    if (url.protocol !== 'https:' || url.username || url.password || (originOnly && (url.pathname !== '/' || url.search || url.hash))) {
      throw new Error(`${name} must be an HTTPS URL${originOnly ? ' with no path, query or fragment' : ' without credentials'}.`);
    }
    return originOnly ? url.origin : url.href;
  };
  const siteUrl = httpsUrl(env.SITE_URL, 'SITE_URL', true);
  const scriptUrl = httpsUrl(env.UMAMI_SCRIPT_URL, 'UMAMI_SCRIPT_URL');
  const websiteId = env.UMAMI_WEBSITE_ID?.trim() || '';
  const domains = env.UMAMI_DOMAINS?.trim() || '';
  if (websiteId && !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(websiteId)) throw new Error('UMAMI_WEBSITE_ID must be a website UUID.');
  return { siteUrl, scriptUrl, websiteId, domains };
}
export async function build({ env = process.env, outDir = resolve(root, 'dist'), quiet = false } = {}) {
  const config = configuration(env);
  if (!quiet && !config.siteUrl) console.warn('SITE_URL is unset: canonical URLs, hreflang, absolute social images and sitemap are omitted. Set your final HTTPS origin and rebuild before indexing.');
  if (!quiet && (!config.scriptUrl || !config.websiteId)) console.warn('Umami is disabled: set both UMAMI_SCRIPT_URL and UMAMI_WEBSITE_ID to enable tracking.');
  await rm(outDir, { recursive: true, force: true });
  await mkdir(resolve(outDir, 'en'), { recursive: true });
  await cp(resolve(root, 'assets'), resolve(outDir, 'assets'), { recursive: true });
  await writeFile(resolve(outDir, 'index.html'), render(content.zh, config));
  await writeFile(resolve(outDir, 'en/index.html'), render(content.en, config));
  await writeFile(resolve(outDir, 'robots.txt'), `User-agent: *\nAllow: /\n${config.siteUrl ? `\nSitemap: ${config.siteUrl}/sitemap.xml\n` : ''}`);
  if (config.siteUrl) {
    const urls = Object.values(content).map(c => `<url><loc>${escape(`${config.siteUrl}${c.path}`)}</loc>${[['zh-CN', '/'], ['en', '/en/'], ['x-default', '/']].map(([lang, path]) => `<xhtml:link rel="alternate" hreflang="${lang}" href="${escape(`${config.siteUrl}${path}`)}"/>`).join('')}</url>`).join('\n');
    await writeFile(resolve(outDir, 'sitemap.xml'), `<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">\n${urls}\n</urlset>\n`);
  }
  await writeFile(resolve(outDir, '404.html'), '<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>404 — FontCrate</title><link rel="stylesheet" href="/assets/style.css"><main class="wrap section"><p class="section-label">FONTCRATE / 404</p><h1>Page not found.<br>页面不存在。</h1><p class="body-copy"><a href="/">返回首页 →</a> · <a href="/en/">English home →</a></p></main></html>');
  if (!quiet) console.log('Built static website: dist/ (Chinese / and English /en/)');
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  build().catch(error => { console.error(error.message); process.exitCode = 1; });
}
