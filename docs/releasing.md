# 构建与发布流程

## 本地准备

1. 修改 `VERSION`，同步用户文档中对应版本的下载文件名。
2. 更新 `docs/release-notes.md`，说明兼容版本及本次变化。
3. 运行构建和发行包检查：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-package.ps1 -LiveProbe
```

没有兼容的微信输入法时，可不传 `-LiveProbe`，只执行包结构与启动检查。
此时输出会明确表示未完成真实输入法连接验证。

构建产物：

```text
dist/
  WeTypeVoiceCapture-v0.1.1-win-x64.exe
  wetype-voice-clipboard-v0.1.1-win-x64.zip
  SHA256SUMS.txt
```

ZIP 中只包含 `WeTypeVoiceCapture.exe`、`README.txt` 和 `LICENSE.txt`。
构建脚本不下载依赖，不提交 Git 内容，也不上传文件。
`dist`、`obj` 和 `artifacts` 均已忽略，避免把本地构建和诊断记录加入源码提交。

## 确认与提交

发布前由维护者审阅源码、兼容范围、验证结果、许可证和待发布的版本。

确认后，将审阅过的内容提交到 `main` 并推送。
等 GitHub Actions 的分支构建通过，再创建与 `VERSION` 一致的标签，例如 `v0.1.1`，并推送该标签。

## GitHub 自动发布

`.github/workflows/build.yml` 会：

1. 使用 Windows 托管运行器编译 EXE 并创建 ZIP、校验文件。
2. 检查实际 EXE 的架构和版本、ZIP 内容以及解压后的程序启动。
3. 将通过检查的文件保存为构建产物。
4. 仅当推送 `v` 开头、且与 `VERSION` 一致的标签时，创建 Release。
5. 发布任务下载刚刚检查过的产物，复核校验值后上传，不重新编译另一份程序。

普通代码提交和拉取请求不会创建 Release。构建任务只有读取仓库的权限，发布任务才拥有创建 Release 所需的写权限。
工作流依赖固定到已核对的 Actions 提交；仓库认证信息不写入 Git 工作区配置。

Release 说明来自 `docs/release-notes.md`。同名 Release 已存在时，命令报错，不静默替换已有下载文件。

## 发布后核对

从 Release 下载单文件 EXE 和 ZIP，核对 `SHA256SUMS.txt`。
在兼容输入法环境中确认托盘启动、正常语音复制、实际取消、远程粘贴以及退出行为。
未验证的场景应在版本说明中明确列出。

GitHub 构建产物下载规则见 [GitHub 文档](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/download-workflow-artifacts)。面向使用者的长期下载入口使用仓库的 Releases 页面。
