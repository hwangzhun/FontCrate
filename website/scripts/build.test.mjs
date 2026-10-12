import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, access, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { build, configuration } from './build.mjs';
const configured = {
  SITE_URL: 'https://fontcrate.example/',
  UMAMI_SCRIPT_URL: 'https://analytics.example/script.js',
  UMAMI_WEBSITE_ID: '94db1cb1-74f4-4a40-ad6c-962362670409',
  UMAMI_DOMAINS: 'fontcrate.example'
};
async function fixture(env, run) {
  const outDir = await mkdtemp(join(tmpdir(), 'fontcrate-site-'));
  try { await build({ env, outDir, quiet: true }); await run(outDir); }
  finally { await rm(outDir, { recursive: true, force: true }); }
}
test('unconfigured build is bilingual, readable without JS, and does not invent an origin', async () => {
  await fixture({}, async dir => {
    for (const [path, lang, alternate] of [['index.html', 'zh-CN', '/en/'], ['en/index.html', 'en', '/']]) {
      const html = await readFile(join(dir, path), 'utf8');
      assert.ok(html.includes(`<html lang="${lang}">`));
      assert.match(html, /<h1[^>]*>.+<br>/);
      assert.match(html, /github\.com\/hwangzhun\/FontCrate\/releases/);
      assert.match(html, new RegExp(`href="${alternate}" class="language-link"`));
      assert.doesNotMatch(html, /rel="canonical"|hreflang=|<script defer|property="og:url"|property="og:image"/);
      assert.match(html, /<details><summary>/);
      const schema = JSON.parse(html.match(/<script type="application\/ld\+json">(.*?)<\/script>/s)[1]);
      assert.equal(schema.operatingSystem, 'Windows 10 1809+ / Windows 11 x64');
      assert.equal(schema.url, undefined);
      assert.equal(schema.inLanguage, lang);
      for (const match of html.matchAll(/(?:href|src)="(\/[^"#]*)"/g)) {
        const target = match[1].endsWith('/') ? `${match[1]}index.html` : match[1];
        await access(join(dir, target));
      }
      for (const match of html.matchAll(/href="#([^"]+)"/g)) assert.ok(html.includes(`id="${match[1]}"`));
    }
    await assert.rejects(access(join(dir, 'sitemap.xml')));
    assert.doesNotMatch(await readFile(join(dir, 'robots.txt'), 'utf8'), /Sitemap:/);
  });
});
test('production build has matching SEO URLs, social assets and Umami event attributes', async () => {
  await fixture(configured, async dir => {
    for (const [path, page] of [['index.html', '/'], ['en/index.html', '/en/']]) {
      const html = await readFile(join(dir, path), 'utf8');
      assert.ok(html.includes(`rel="canonical" href="https://fontcrate.example${page}"`));
      assert.match(html, /hreflang="zh-CN" href="https:\/\/fontcrate\.example\/"/);
      assert.match(html, /hreflang="en" href="https:\/\/fontcrate\.example\/en\/"/);
      assert.match(html, /hreflang="x-default"/);
      assert.match(html, /og:image" content="https:\/\/fontcrate\.example\/assets\/social-cover.png/);
      assert.match(html, /<script defer src="https:\/\/analytics\.example\/script.js" data-website-id="94db1cb1-74f4-4a40-ad6c-962362670409" data-domains="fontcrate.example"><\/script>/);
      for (const event of ['download', 'github', 'guide', 'language-switch']) assert.ok(html.includes(`data-umami-event="${event}"`));
      for (const match of html.matchAll(/<a[^>]*data-umami-event=[^>]*>/g)) {
        assert.match(match[0], /data-umami-event-language=/);
        assert.match(match[0], /data-umami-event-position=/);
      }
    }
    const sitemap = await readFile(join(dir, 'sitemap.xml'), 'utf8');
    assert.match(sitemap, /<loc>https:\/\/fontcrate.example\/<\/loc>/);
    assert.match(sitemap, /<loc>https:\/\/fontcrate.example\/en\/<\/loc>/);
    assert.match(await readFile(join(dir, 'robots.txt'), 'utf8'), /Sitemap: https:\/\/fontcrate.example\/sitemap.xml/);
    await access(join(dir, 'assets/social-cover.png'));
  });
});
test('partial analytics configuration never inserts a tracking script', async () => {
  for (const env of [{ UMAMI_SCRIPT_URL: configured.UMAMI_SCRIPT_URL }, { UMAMI_WEBSITE_ID: configured.UMAMI_WEBSITE_ID }]) {
    await fixture(env, async dir => assert.doesNotMatch(await readFile(join(dir, 'index.html'), 'utf8'), /<script defer/));
  }
});
test('configuration rejects invalid URLs or UUIDs and escapes domain attributes', async () => {
  for (const env of [{ SITE_URL: 'https://example.com/en/' }, { SITE_URL: 'http://example.com' }, { SITE_URL: 'https://user:password@example.com' }, { SITE_URL: 'invalid' }, { UMAMI_SCRIPT_URL: 'javascript:alert(1)' }, { UMAMI_WEBSITE_ID: 'not-a-uuid' }]) assert.throws(() => configuration(env));
  await fixture({ ...configured, UMAMI_DOMAINS: 'example.com" onload="bad' }, async dir => {
    const html = await readFile(join(dir, 'index.html'), 'utf8');
    assert.match(html, /data-domains="example.com&quot; onload=&quot;bad"/);
  });
});
