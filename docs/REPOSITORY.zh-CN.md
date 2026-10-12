# 仓库上传说明

将 `FontCrate` 目录的内容作为独立仓库根目录；首页为 `README.md`，详细中文说明为 `README.zh-CN.md`。

## 上传内容

提交源码、测试、工具脚本、文档、图标、许可证、NuGet 锁文件及 `.github` 工作流。`.gitignore` 排除 `bin/`、`obj/`、`artifacts/`、`TestResults/`、数据库和本地配置。

原 `FontLite` 目录保留在本地作为整理前备份，含旧构建、测试报告和 Wine 环境。不要将父级 REA 目录或该备份一起上传。验收文档中引用的历史测试报告与 Wine 日志为本地证据，不包含在源码仓库中。

## 首次提交

此目录已初始化本地 Git 仓库，分支为 `main`。在此目录检查并提交：

```sh
git status --short
git add .
git diff --cached --stat
git commit -m "Initial FontCrate source"
```

远程仓库为 https://github.com/hwangzhun/FontCrate。本地已配置 `origin`，完成提交后执行：

```sh
git push -u origin main
```

上传压缩包时先解压，将内部文件提交到仓库根目录，并确保 `.github`、`.gitignore`、`.gitattributes` 一起上传。CI 尚需上传后实际运行确认。

## 发行包

`tools/publish.ps1` 生成 `FontCrate-win-x64.zip` 和对应的 `FontCrate-source.zip`；发布时将二者放入同一次 Release。二进制发行包使用 Release 附件分发。整理用的 `FontCrate-upload.zip` 是源码快照，不是已有二进制的对应源码声明。

应用关于页、项目元数据和 README 中的链接均指向 FontCrate 仓库。
