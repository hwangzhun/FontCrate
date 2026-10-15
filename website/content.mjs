export const content = {
  zh: {
    lang: 'zh-CN', locale: 'zh_CN', path: '/', alternate: '/en/', language: 'English',
    title: 'FontCrate — Windows 字体管理，按项目整理、按需启用',
    description: '用 FontCrate 集中浏览和预览字体，以标签与收藏整理项目用字，需要时临时启用。面向 Windows 10 1809+ / Windows 11 x64 的开源字体管理工具。',
    skip: '跳到主要内容', nav: ['功能', '使用流程', '常见问题'], download: '获取 Windows 版本', github: '查看 GitHub', guide: '阅读使用说明',
    eyebrow: '给每一次设计，留好字体的位置。', hero: ['把字体整理好，', '需要时再加载。'],
    intro: '收藏可以很丰富，工作环境可以很精简。FontCrate 帮你预览、分类与挑选字体，把项目需要的那几款，带进设计软件。',
    platform: 'Windows 10 1809+ / Windows 11 x64', license: '开源 · GPL v3', status: '开发验证中 · 下载前请查看发布说明',
    demo: '功能示意 · 非应用截图', collection: '项目标签', project: '周末海报', active: '按需启用', sample: '让文字有自己的表情。', sampleSub: 'A place for every type.', tags: ['标题', '正文', '已收藏'],
    formats: '支持的字体格式',
    problemLabel: '01 / 从收藏到创作', problemTitle: '字体留在库里，\n灵感带进项目。',
    problemText: '不是每一款收藏的字体，都需要长期安装。先在自己的文件夹里保留完整资源，再为当前任务挑选与启用，让项目用字更有条理。',
    problemAside: '一份字体收藏，\n许多种项目组合。',
    featureLabel: '02 / 你的字体工作台', featureTitle: '看得清，找得到，\n用得刚刚好。',
    features: [
      ['01', '集中浏览', '添加一个或多个字体文件夹，在同一个库里搜索和筛选。加入字体库不会自动安装字体。'],
      ['02', '用自己的文字预览', '输入实际要排版的内容，比较字体效果，查看字形与字符覆盖，再做选择。'],
      ['03', '围绕项目整理', '用标签和收藏整理风格、用途与项目。字体原文件继续保留在自己的目录里。'],
      ['04', '按需启用', '为当前任务临时启用字体，完成后停用；经常使用的字体也可安装到当前 Windows 用户。']
    ],
    workflowLabel: '03 / 一个顺手的流程', workflowTitle: '下一次创作，\n从整理好的字体开始。',
    steps: [ ['添加', '选择字体文件夹，等待扫描完成。'], ['挑选', '预览实际文案，把合适的字体加入项目标签。'], ['启用', '临时启用选中的字体，在设计软件中使用。'], ['停用', '完成后停用，原文件和分类信息继续保留。'] ],
    workflowNote: '部分软件会缓存字体列表，启用后可能需要重新打开软件。',
    faqLabel: '04 / 开始之前', faqTitle: '关于使用，你可能想知道。',
    faqs: [
      ['需要什么系统？', '面向 Windows 10 1809 及以上和 Windows 11，均为 x64；最低系统内部版本为 17763。Windows 10 / 11 实机验收仍待完成。运行 EXE 安装包后，从开始菜单或可选桌面快捷方式启动。仅安装到当前用户，无需另外安装 .NET，也不要求管理员权限。'],
      ['临时启用和安装有什么区别？', '临时启用适合当前任务，可以手动停用，正常完全退出时也会停用；最小化到托盘会保持启用。安装适合常用字体，退出后仍保持安装。TTC / OTC 按整个文件启用或停用。'],
      ['整理字体会删除原文件吗？', '移除来源、删除标签不会删除原始字体文件。卸载只处理 FontCrate 自己安装的字体。'],
      ['支持哪些字体？', '支持 TTF、OTF、TTC 和 OTC。变量字体使用默认实例，目前不提供字体轴调节；不支持 WOFF / WOFF2 预览和云同步。'],
    ],
    openLabel: '由 Hwangzhun 开发 · GPL-3.0-only', ctaTitle: '给你的字体收藏，\n一个有条理的家。', ctaText: '了解源码、试用构建，或把使用反馈带回项目。', footer: '把字体整理好，需要时再加载。', source: '源代码', releases: '发布说明', privacy: '本站配置 Umami 后会统计访问及按钮点击，不发送字体文件或预览文字。'
  },
  en: {
    lang: 'en', locale: 'en_US', path: '/en/', alternate: '/', language: '简体中文',
    title: 'FontCrate — Organize Windows fonts by project, activate on demand',
    description: 'Browse and preview your font collection with FontCrate. Organize fonts with tags and favorites, then activate them on demand. Open source for Windows 10 1809+ / Windows 11 x64.',
    skip: 'Skip to main content', nav: ['Features', 'Workflow', 'FAQ'], download: 'Get the Windows build', github: 'View on GitHub', guide: 'Read the guide (Chinese)',
    eyebrow: 'A little more order. A lot more room to create.', hero: ['A home for your fonts.', 'Ready when you are.'],
    intro: 'Keep a rich collection and a focused workspace. Preview, organize and choose your fonts, then bring just the ones your project needs into your design tools.',
    platform: 'Windows 10 1809+ / Windows 11 x64', license: 'Open source · GPL v3', status: 'In development · Read the release notes before downloading',
    demo: 'Feature illustration · Not an app screenshot', collection: 'PROJECT TAG', project: 'Weekend poster', active: 'Activate on demand', sample: 'Give words a little character.', sampleSub: 'A place for every type.', tags: ['Display', 'Body text', 'Favorite'],
    formats: 'Supported font formats',
    problemLabel: '01 / From collecting to creating', problemTitle: 'Keep fonts in your library.\nBring ideas to your projects.',
    problemText: 'Every font you collect doesn’t need to stay installed. Keep your original files in your own folders, then choose and activate fonts for the task at hand. Give every project a considered set of type.',
    problemAside: 'One font collection.\nMany creative combinations.',
    featureLabel: '02 / Your type workspace', featureTitle: 'See it. Find it.\nUse what you need.',
    features: [
      ['01', 'One place to browse', 'Add one or more font folders and search or filter them in a single library. Adding fonts to the library does not install them.'],
      ['02', 'Preview your own words', 'Try the text you are actually setting. Compare styles, inspect glyphs and character coverage, then make your choice.'],
      ['03', 'Organize around projects', 'Use tags and favorites for styles, purposes and projects. Original font files stay in your own folders.'],
      ['04', 'Activate on demand', 'Temporarily activate fonts for a task, then deactivate them when done. Install frequently used fonts for the current Windows user.']
    ],
    workflowLabel: '03 / A simple rhythm', workflowTitle: 'Start your next project\nwith your fonts in order.',
    steps: [ ['Add', 'Choose a font folder and let the scan finish.'], ['Choose', 'Preview real copy and collect the right fonts for your project.'], ['Activate', 'Temporarily activate selected fonts for use in your design tools.'], ['Deactivate', 'When finished, deactivate them. Files and organization stay intact.'] ],
    workflowNote: 'Some apps cache their font lists and may need to be reopened after activation.',
    faqLabel: '04 / Before you begin', faqTitle: 'A few things worth knowing.',
    faqs: [
      ['What system do I need?', 'FontCrate targets Windows 10 version 1809 or later (build 17763+) and Windows 11, both x64. Validation on Windows 10 / 11 machines is still pending. Run the EXE installer, then launch from the Start menu or optional desktop shortcut. Installation is for the current user. No separate .NET installation or administrator privileges are required.'],
      ['How does temporary activation differ from installation?', 'Temporary activation is for the current task. Deactivate manually or fully exit normally to deactivate fonts. Minimizing to the tray keeps them active. Installed fonts remain installed after exit. TTC / OTC collections are activated or deactivated as a whole file.'],
      ['Will organizing fonts delete my files?', 'Removing a source or tag does not delete original font files. Uninstallation only handles fonts installed by FontCrate itself.'],
      ['Which formats are supported?', 'TTF, OTF, TTC and OTC are supported. Variable fonts use their default instance; axis controls are not available yet. WOFF / WOFF2 previews and cloud sync are not supported.'],
    ],
    openLabel: 'Made by Hwangzhun · GPL-3.0-only', ctaTitle: 'Make room\nfor your next idea.', ctaText: 'Explore the source, try a build, or share your experience with the project.', footer: 'Organize your fonts. Activate when needed.', source: 'Source code', releases: 'Release notes', privacy: 'When configured, this site uses Umami to measure visits and button clicks. Font files and preview text are not sent.'
  }
};
