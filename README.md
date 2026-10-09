# 微信语音复制 · WeType Voice Clipboard

[![Windows build](https://github.com/lzhshen/wetype-voice-clipboard/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/lzhshen/wetype-voice-clipboard/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/lzhshen/wetype-voice-clipboard)](https://github.com/lzhshen/wetype-voice-clipboard/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

在 Windows 后台读取微信输入法已经完成的语音识别结果，自动复制到本机剪贴板。
你仍然使用微信输入法原来的语音快捷键，并在需要时手动粘贴。

适用于通过向日葵等远程控制软件操作另一台电脑时，语音已经识别出来，却没有进入宿主机剪贴板的情况。

**当前仅适配微信输入法 Windows 2.1.4.6 的一个经过验证的程序版本。**
工具会核对程序文件的 SHA-256；文件不匹配时停止读取，不会尝试猜测内部位置。
这是非官方兼容工具，与腾讯或微信没有隶属关系。

## 下载使用

从 [Releases](https://github.com/lzhshen/wetype-voice-clipboard/releases) 下载：

- [单文件 EXE](https://github.com/lzhshen/wetype-voice-clipboard/releases/download/v0.1.0/WeTypeVoiceCapture-v0.1.0-win-x64.exe)：下载后直接双击运行。
- [ZIP 便携包](https://github.com/lzhshen/wetype-voice-clipboard/releases/download/v0.1.0/wetype-voice-clipboard-v0.1.0-win-x64.zip)：解压后运行 `WeTypeVoiceCapture.exe`，附使用说明和许可证。
- [SHA256SUMS.txt](https://github.com/lzhshen/wetype-voice-clipboard/releases/download/v0.1.0/SHA256SUMS.txt)：下载文件的校验值。

环境要求：Windows 10/11 x64、.NET Framework 4.8，以及受支持的微信输入法程序。
无需安装 Python、Node.js、Visual Studio 或额外的语音模型。
Release 中的程序目前未进行代码签名。

1. 启动微信输入法，再双击下载的程序。
2. 工具在系统托盘运行，没有主窗口。鼠标悬停可以查看当前状态。
3. 照常使用微信输入法的语音快捷键，例如 Alt+Q 开始，再按 Alt+Q 结束。
4. 等待约 1～2 秒后，在本机或远程电脑的输入框粘贴。

通过向日葵远程粘贴时，需保持原有的跨设备剪贴板通路可用；工具本身只写入 Windows 剪贴板。

右键托盘图标可以选择“暂停复制”“恢复复制”或“退出”。不自动设置开机启动，重复运行只保留一个实例。
如果之前运行着本工具的测试版本，先从托盘退出旧版本，再启动下载的新版本。

## 工作方式

```text
你说话 → 微信输入法生成最终文字 → 后台工具读取 → Windows 剪贴板 → 手动粘贴
```

工具以只读方式访问微信输入法进程，检查识别状态、取消状态和最终文字。
识别完成且文字稳定后，每轮复制一次。启动时不会重新复制已结束的旧语音。
取消操作由微信输入法处理，工具按其实际状态判断，不改变快捷键含义。

- 不录音、不启动或关闭语音，不注册快捷键，不切换焦点，不自动粘贴。
- 不修改微信输入法的文件或运行内存，不注入代码。
- 工具自身不联网，不上传或保存识别文字历史。
- 仅将当前运行状态和复制次数保存在 `%LOCALAPPDATA%\WeTypeVoiceCapture\status.txt`。

**正常语音完成后，会替换剪贴板中的原内容。**

## 兼容范围

| 项目 | 当前支持情况 |
| --- | --- |
| 工具运行位置 | Windows 宿主机，x64 |
| 微信输入法 | 2.1.4.6，且 `wetype_update.exe` 文件校验一致 |
| 输出 | Windows Unicode 文本剪贴板 |
| 远程使用 | 已在 Windows 通过向日葵控制 Mac 的场景实测 |
| 自动恢复 | 输入法重新启动后尝试重新连接；版本不匹配时停止读取 |
| macOS 原生运行 | 不支持；Mac 可以作为远程粘贴目标 |

这不是微信官方开放接口。输入法升级可能改变内部结构，需要重新适配。
已知版本信息和维护说明见 [兼容性说明](docs/compatibility.md)。

## 本地构建

在 Windows PowerShell 5.1 或 PowerShell 7 中运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-package.ps1
```

这里的执行策略参数只作用于本次脚本进程，不修改系统设置。
构建使用 Windows 随 .NET Framework 提供的 C# 编译器，不下载依赖。
生成的单文件程序、ZIP 和校验文件位于 `dist`，不加入源码提交。

本机已安装兼容输入法时，还可以进行只读连接验证：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-package.ps1 -LiveProbe
```

该验证不启动语音、不切换焦点、不写入剪贴板，只检查能否读取当前语音对象和状态。

## 发布

版本号保存在 `VERSION`。GitHub Actions 会在代码提交和拉取请求中构建并检查发布包。
维护者确认后推送与 `VERSION` 一致的版本标签，例如 `v0.1.0`，工作流才会创建 GitHub Release 并上传下载文件。
普通提交只构建和检查，不会创建 Release；版本标签的发布任务上传的就是同一轮 CI 已检查过的文件。

详见 [发布流程](docs/releasing.md) 和 [验证记录](docs/validation.md)。

## 许可证

[MIT](LICENSE)。
