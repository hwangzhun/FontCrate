# FontCrate 介绍站

中文首页 `/`，英文首页 `/en/`。纯静态 HTML、CSS 和本地图片，页面内容及语言切换不依赖 JavaScript；唯一的浏览器脚本是配置后加载的 Umami。没有 npm 第三方依赖，不需要安装步骤或后端。

## 本地构建与预览

需要 Node.js 22 或更高版本：

```bash
cd website
npm run build
npm test
npm run preview
```

打开 http://127.0.0.1:4173 。`dist/` 是可以直接上传的完整网站，未纳入 Git。预览服务仅监听本机；可用 `PORT` 改端口。默认构建不启用统计，也不生成需要正式域名的 SEO URL。

修改 `content.mjs` 更新双语文案，`template.mjs` 更新 HTML，`assets/style.css` 更新样式。现有 `assets/` 包含优化后的项目 Logo、图标及 1200×630 分享封面。页面中的字体标签是功能示意，不是软件截图。

## 环境变量

在 EdgeOne 项目设置中配置，或在本地构建前导出。`.env.example` 是说明模板，构建脚本不会自动读取 `.env`。

| 变量 | 填写内容 | 缺失时行为 |
| --- | --- | --- |
| `SITE_URL` | 正式网站 HTTPS 根地址，如 `https://你的域名`，不含路径、查询参数或片段 | 不生成 canonical、hreflang、sitemap 和绝对社交图片 URL，并输出警告 |
| `UMAMI_SCRIPT_URL` | Umami 提供的完整 HTTPS script `src` | 统计关闭 |
| `UMAMI_WEBSITE_ID` | Umami 网站 UUID，即 `data-website-id` | 统计关闭 |
| `UMAMI_DOMAINS` | 可选域名列表，与 `data-domains` 一致，逗号分隔，不含协议 | 不限制统计域名 |

`UMAMI_SCRIPT_URL` 和 `UMAMI_WEBSITE_ID` 同时存在才插入 `defer` 统计脚本。把你从 Umami 复制的 script 中的参数对应填入即可；不会创建 Umami 网站或自动上传任何字体数据。无效 HTTPS 地址或网站 UUID 会使构建报错，以免发布错误配置。以上参数是公开站点配置，请勿填入 API 密钥或账号凭据。

本地生产配置示例（用自己的实际值替换）：

```bash
SITE_URL='https://你的域名' \
UMAMI_SCRIPT_URL='https://你的统计域名/script.js' \
UMAMI_WEBSITE_ID='你的网站UUID' \
UMAMI_DOMAINS='你的域名' \
npm run build
```

配置 Umami 后，自动统计浏览量并通过按钮属性记录下列事件：

| 事件 | 操作 | 事件属性 |
| --- | --- | --- |
| `download` | 进入 GitHub Releases（含发布说明） | `language`、`position` |
| `github` | 进入项目仓库 | `language`、`position` |
| `guide` | 打开中文使用说明 | `language`、`position` |
| `language-switch` | 切换中英文页面 | 当前 `language`、`position` |

`position` 区分 `hero`、`header`、`workflow`、`footer`、`footer-links`。`download` 衡量下载入口点击，不代表安装包已下载成功。语言切换记录离开页面的语言。广告拦截、统计服务故障或 JavaScript 关闭均不影响链接；被拦截时不会有统计记录。

## 部署到 EdgeOne Pages

### 连接 GitHub 仓库（推荐）

1. 在 EdgeOne Pages 新建项目，连接 `hwangzhun/FontCrate`，选择要部署的分支。
2. **项目根目录填 `website`**，框架选“其他/自定义”，构建命令 `npm run build`，输出目录 `dist`，Node 版本选择 22。无需安装依赖；如界面要求安装命令，可填 `npm install --ignore-scripts`。
3. `website/edgeone.json` 已声明构建命令、输出目录和 Node 22.11.0。它应位于选定的项目根目录。不要让 EdgeOne 构建仓库中的 .NET 应用。
4. 首次可以不填统计和域名配置，部署成功后确认 EdgeOne 分配的地址及 `/en/` 都可访问。
5. 绑定自定义域名，按平台提示配置 DNS 与 HTTPS；也可使用平台分配的实际域名。将最终 HTTPS 地址填入 `SITE_URL`。
6. 填入真实 Umami 参数。若使用 `UMAMI_DOMAINS`，需包含正式域名；预览域名不在列表里就不会统计。预览部署建议关闭统计，避免污染正式数据。
7. **重新构建并部署**。这些变量是在构建时写入静态页面，改配置或域名之后不能只刷新浏览器。

没有单页应用重写规则；`/` 与 `/en/` 都有独立 HTML 文件。未知地址使用静态 `404.html`，不要配置所有路径回退首页。

### 直接上传

在本地设置环境变量后运行 `npm run build`，上传 `dist/` 的内容，保证上传根目录直接包含 `index.html`、`en/`、`assets/` 和 `robots.txt`，不要把仓库源码当作网站上传。配置 `SITE_URL` 时还应包含 `sitemap.xml`。修改配置后重新构建并上传。

## 上线检查

- 在桌面及手机访问 `/`、`/en/`；检查语言切换、FAQ 展开、下载及文档链接。下载统一进入 GitHub 发布页，发布内容由仓库维护者管理。
- 查看源代码，确认两种页面的标题、描述、canonical、hreflang、分享图片地址及结构化数据使用正式域名。
- 访问 `/robots.txt`、`/sitemap.xml`、`/assets/social-cover.png`；提交站点地图到需要的搜索引擎站长平台。SEO 配置不保证收录或排名。
- 打开网络面板，确认真实 Umami 脚本成功加载，访问与按钮点击事件出现在 Umami 控制台。检查浏览器没有启用广告拦截且域名白名单匹配。
- 分享正式地址，检查卡片标题及图片。迁移域名时同步修改 `SITE_URL`、Umami 网站及域名列表后重新构建。

真实 Umami 采集与公网 SEO 验收需正式配置和部署后完成，本地测试不证明统计后台已经收到事件。

官方参考：[EdgeOne 项目配置](https://pages.edgeone.ai/document/edgeone-json)、[Umami 接入](https://docs.umami.is/docs/collect-data)、[Umami 点击事件](https://docs.umami.is/docs/track-events)。
