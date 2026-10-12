export const repo = 'https://github.com/hwangzhun/FontCrate';
export const escape = value => String(value).replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[char]));
const lines = value => escape(value).replace(/\n/g, '<br>');
const link = (href, text, event, position, lang, className = '') => `<a href="${escape(href)}" class="${className}" data-umami-event="${event}" data-umami-event-language="${lang}" data-umami-event-position="${position}">${escape(text)}</a>`;
export function render(c, { siteUrl, scriptUrl, websiteId, domains }) {
  const absolute = path => `${siteUrl}${path}`;
  const download = (position, secondary = false) => link(`${repo}/releases`, c.download, 'download', position, c.lang, `button ${secondary ? 'button-light' : 'button-primary'}`);
  const github = position => link(repo, c.github, 'github', position, c.lang, 'text-link');
  const guide = position => link(`${repo}/blob/main/docs/USAGE.zh-CN.md`, c.guide, 'guide', position, c.lang, 'text-link');
  const seo = siteUrl ? `
    <link rel="canonical" href="${escape(absolute(c.path))}">
    <link rel="alternate" hreflang="zh-CN" href="${escape(absolute('/'))}">
    <link rel="alternate" hreflang="en" href="${escape(absolute('/en/'))}">
    <link rel="alternate" hreflang="x-default" href="${escape(absolute('/'))}">
    <meta property="og:url" content="${escape(absolute(c.path))}">
    <meta property="og:image" content="${escape(absolute('/assets/social-cover.png'))}">
    <meta property="og:image:width" content="1200">
    <meta property="og:image:height" content="630">
    <meta name="twitter:image" content="${escape(absolute('/assets/social-cover.png'))}">` : '';
  const schema = {
    '@context': 'https://schema.org', '@type': 'SoftwareApplication', name: 'FontCrate',
    description: c.description, operatingSystem: 'Windows 10 1809+ / Windows 11 x64', applicationCategory: 'DesignApplication',
    inLanguage: c.lang, license: `${repo}/blob/main/LICENSE`, author: { '@type': 'Person', name: 'Hwangzhun' },
    downloadUrl: `${repo}/releases`, ...(siteUrl ? { url: absolute(c.path), image: absolute('/assets/social-cover.png') } : {})
  };
  const analytics = scriptUrl && websiteId ? `<script defer src="${escape(scriptUrl)}" data-website-id="${escape(websiteId)}"${domains ? ` data-domains="${escape(domains)}"` : ''}></script>` : '';
  return `<!doctype html>
<html lang="${c.lang}">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>${escape(c.title)}</title>
  <meta name="description" content="${escape(c.description)}">
  <meta name="theme-color" content="#f6f5f0">
  <meta property="og:type" content="website">
  <meta property="og:site_name" content="FontCrate">
  <meta property="og:locale" content="${c.locale}">
  <meta property="og:locale:alternate" content="${c.lang === 'en' ? 'zh_CN' : 'en_US'}">
  <meta property="og:title" content="${escape(c.title)}">
  <meta property="og:description" content="${escape(c.description)}">
  <meta property="og:image:alt" content="FontCrate — Organize your fonts. Activate when needed.">
  <meta name="twitter:card" content="summary_large_image">
  <meta name="twitter:title" content="${escape(c.title)}">
  <meta name="twitter:description" content="${escape(c.description)}">${seo}
  <link rel="icon" type="image/png" href="/assets/favicon.png">
  <link rel="apple-touch-icon" href="/assets/apple-touch-icon.png">
  <link rel="stylesheet" href="/assets/style.css">
  <script type="application/ld+json">${JSON.stringify(schema).replace(/</g, '\\u003c')}</script>
  ${analytics}
</head>
<body>
  <a class="skip-link" href="#main">${c.skip}</a>
  <header class="site-header wrap">
    <a class="brand" href="${c.path}" aria-label="FontCrate"><img src="/assets/logo.webp" alt="" width="42" height="42"><span>FontCrate<span class="brand-dot">.</span></span></a>
    <nav aria-label="${c.lang === 'en' ? 'Main navigation' : '主导航'}">
      <a href="#features">${c.nav[0]}</a><a href="#workflow">${c.nav[1]}</a><a href="#faq">${c.nav[2]}</a>
    </nav>
    ${link(c.alternate, c.language, 'language-switch', 'header', c.lang, 'language-link')}
  </header>
  <main id="main">
    <section class="hero wrap" aria-labelledby="hero-title">
      <div class="hero-copy">
        <p class="eyebrow"><span class="tiny-square"></span>${c.eyebrow}</p>
        <h1 id="hero-title">${escape(c.hero[0])}<br><span>${escape(c.hero[1])}</span></h1>
        <p class="hero-intro">${escape(c.intro)}</p>
        <div class="actions">${download('hero')}${github('hero')}</div>
        <p class="platform"><span class="windows-icon" aria-hidden="true">⊞</span>${c.platform}<span class="separator">/</span>${c.license}</p>
        <p class="status"><span aria-hidden="true">◌</span> ${c.status}</p>
      </div>
      <figure class="type-figure">
        <div class="specimen-board">
          <div class="board-header"><span>THE TYPE COLLECTION</span><span>FC / 001</span></div>
          <div class="big-type" aria-hidden="true"><span>A</span><span>a</span><span class="type-dot">.</span></div>
          <div class="board-caption"><span>SERIF / SANS / EXPRESSION</span><span>字</span></div>
          <div class="collection-card">
            <div class="collection-meta"><span>${c.collection}</span><span class="active-dot">${c.active}</span></div>
            <h2>${c.project}<span aria-hidden="true">↗</span></h2>
            <p class="sample-serif">${c.sample}</p><p class="sample-sans">${c.sampleSub}</p>
            <div class="tag-list">${c.tags.map(t => `<span>${escape(t)}</span>`).join('')}<span class="file-tag">OTF</span></div>
          </div>
        </div>
        <figcaption>${c.demo}</figcaption>
      </figure>
    </section>
    <div class="format-strip wrap"><span>${c.formats}</span><div><span>TTF</span><span>OTF</span><span>TTC</span><span>OTC</span></div><span class="format-note">LOCAL FILES. YOUR COLLECTION.</span></div>
    <section class="section problem wrap" aria-labelledby="problem-title">
      <div><p class="section-label">${c.problemLabel}</p><h2 id="problem-title">${lines(c.problemTitle)}</h2><p class="body-copy">${c.problemText}</p></div>
      <div class="project-illustration"><div class="type-stack" aria-hidden="true"><span>Aa</span><span>字</span><span>Ag</span></div><p>${lines(c.problemAside)}</p><span class="diagram-mark" aria-hidden="true">COLLECT → CURATE → CREATE</span></div>
    </section>
    <section class="features section" id="features" aria-labelledby="features-title"><div class="wrap">
      <p class="section-label">${c.featureLabel}</p><div class="section-heading"><h2 id="features-title">${lines(c.featureTitle)}</h2><span class="decorative-type" aria-hidden="true">Aa Bb Cc</span></div>
      <div class="feature-grid">${c.features.map(([n, title, body]) => `<article><span class="feature-number">${n}</span><h3>${title}</h3><p>${body}</p></article>`).join('')}</div>
    </div></section>
    <section class="section workflow wrap" id="workflow" aria-labelledby="workflow-title">
      <p class="section-label">${c.workflowLabel}</p><h2 id="workflow-title">${lines(c.workflowTitle)}</h2>
      <ol class="steps">${c.steps.map(([title, body], i) => `<li><span class="step-number">0${i + 1}</span><h3>${title}</h3><p>${body}</p></li>`).join('')}</ol>
      <div class="workflow-bottom"><p>${c.workflowNote}</p>${guide('workflow')}</div>
    </section>
    <section class="section faq wrap" id="faq" aria-labelledby="faq-title"><div><p class="section-label">${c.faqLabel}</p><h2 id="faq-title">${c.faqTitle}</h2><p class="faq-mark" aria-hidden="true">?</p></div><div class="faq-list">${c.faqs.map(([q, a]) => `<details><summary>${q}<span aria-hidden="true">+</span></summary><p>${a}</p></details>`).join('')}</div></section>
    <section class="cta" aria-labelledby="cta-title"><div class="wrap cta-inner"><div><p class="section-label">${c.openLabel}</p><h2 id="cta-title">${lines(c.ctaTitle)}</h2><p>${c.ctaText}</p><div class="actions">${download('footer', true)}${github('footer')}</div></div><span class="cta-type" aria-hidden="true">F<span>c</span>.</span></div></section>
  </main>
  <footer class="wrap site-footer"><div><a class="footer-brand" href="${c.path}">FontCrate.</a><p>${c.footer}</p></div><div class="footer-links">${link(repo, c.source, 'github', 'footer-links', c.lang)}${link(`${repo}/releases`, c.releases, 'download', 'footer-links', c.lang)}${guide('footer-links')}</div><div class="footer-bottom"><span>© ${new Date().getUTCFullYear()} Hwangzhun · GPL-3.0-only</span>${analytics ? `<p>${c.privacy}</p>` : ''}</div></footer>
</body>
</html>`;
}
