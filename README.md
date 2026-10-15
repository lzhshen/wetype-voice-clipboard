# 微信语音复制 · WeType Voice Clipboard

<img src="assets/app-icon.png" width="96" height="96" alt="蓝色叠层语音气泡图标">

[![Windows build](https://github.com/lzhshen/wetype-voice-clipboard/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/lzhshen/wetype-voice-clipboard/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/lzhshen/wetype-voice-clipboard)](https://github.com/lzhshen/wetype-voice-clipboard/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

在 Windows 后台读取微信输入法已经完成的语音识别结果，自动复制到本机剪贴板。
你仍然使用微信输入法原来的语音快捷键，并在需要时手动粘贴。

适用于通过向日葵等远程控制软件操作另一台电脑时，语音已经识别出来，却没有进入宿主机剪贴板的情况。

**v0.2.4 适配微信输入法 Windows 2.1.4.6 和 3.0.0.34 的已验证程序文件。**
工具会核对程序文件的 SHA-256；文件不匹配时停止读取，不会尝试猜测内部位置。
这是非官方兼容工具，与腾讯或微信没有隶属关系。

v0.2.4 将剪贴板纯文本改为一次性写入，避免 OLE 分阶段写入时首次变化通知遇到占用。自动检查与远端验收结果见 [验证记录](docs/validation.md)。

## 下载使用

从 [Releases](https://github.com/lzhshen/wetype-voice-clipboard/releases) 下载：

- [单文件 EXE](https://github.com/lzhshen/wetype-voice-clipboard/releases/download/v0.2.4/WeTypeVoiceCapture-v0.2.4-win-x64.exe)：下载后直接双击运行。
- [ZIP 便携包](https://github.com/lzhshen/wetype-voice-clipboard/releases/download/v0.2.4/wetype-voice-clipboard-v0.2.4-win-x64.zip)：解压后运行 `WeTypeVoiceCapture.exe`，附使用说明和许可证。
- [SHA256SUMS.txt](https://github.com/lzhshen/wetype-voice-clipboard/releases/download/v0.2.4/SHA256SUMS.txt)：下载文件的校验值。

环境要求：Windows 10/11 x64、.NET Framework 4.8，以及受支持的微信输入法程序。
无需安装 Python、Node.js、Visual Studio 或额外的语音模型。
Release 中的程序目前未进行代码签名。

1. 启动微信输入法，再双击下载的程序。
2. 工具以蓝色叠层语音气泡图标在系统托盘运行，没有主窗口。鼠标悬停可以查看工具版本号和当前状态，右键菜单顶部也会显示工具版本号。
3. 照常使用微信输入法的语音快捷键，例如 Alt+Q 开始，再按 Alt+Q 结束。
4. 等待语音处理完成、托盘状态显示“已复制”（通常约 1～2 秒）后，在本机或远程电脑的输入框粘贴。处理完成前粘贴，仍会得到原剪贴板内容。

通过向日葵远程粘贴时，需保持原有的跨设备剪贴板通路可用；工具本身只写入 Windows 剪贴板。

右键托盘图标可以选择“暂停复制”“恢复复制”“开机启动”或“退出”。重复运行只保留一个实例。
如果之前运行着本工具的测试版本，先从托盘退出旧版本，再启动下载的新版本。

## 开机启动配置

开机启动功能从 v0.2.0 起提供。

1. 先把 EXE（或解压后的便携包）放到准备长期保留的目录，再运行程序。
2. 右键托盘图标，点击“开机启动”。菜单中出现勾选，表示已保存启动设置。
3. 下次当前用户登录 Windows 时，工具会在后台托盘运行；微信输入法尚未启动时会自动等待并连接。
4. 再次点击“开机启动”取消勾选，即可关闭。选择“退出”只结束本次运行，不会取消开机启动。

首次运行默认关闭，无需管理员权限。设置保存在当前用户的 Windows 启动项中：
`HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` 下的 `WeTypeVoiceCapture`，内容为带引号的 EXE 完整路径。
移动或升级后，先退出旧实例，再手动运行新位置的程序，已有的启动设置会自动更新到新路径。
删除程序前请先取消勾选。若曾在 Windows“启动应用”或任务管理器中禁用本工具，也需在那里恢复启用。

## 工作方式

```text
你说话 → 微信输入法生成最终文字 → 后台工具读取 → Windows 剪贴板 → 手动粘贴
```

工具以只读方式访问微信输入法进程，检查识别状态、取消状态和最终文字。
识别完成且文字稳定后，每轮复制一次。启动时不会重新复制已结束的旧语音。
即使轮询错过短暂的结束处理中状态，也会复制已经观察到的新一轮语音的最终结果，避免剪贴板停留在上一轮。
取消操作由微信输入法处理，工具按其实际状态判断，不改变快捷键含义。

正常完成后，无论光标在输入框内还是输入框外，工具都应更新 Windows 剪贴板。
微信输入法本身可能直接把文字送入输入框，也可能采用剪贴板输出；本工具独立读取最终结果，补上复制步骤。
因此“文字进入输入框”不能证明本工具已复制成功。托盘状态和复制次数用于确认本工具是否工作。

- 不录音、不启动或关闭语音，不注册快捷键，不切换焦点，不自动粘贴。
- 不修改微信输入法的文件或运行内存，不注入代码。
- 工具自身不联网，不上传或保存识别文字历史。
- 当前运行状态和复制次数保存在 `%LOCALAPPDATA%\WeTypeVoiceCapture\status.txt`；开机启动设置保存在当前用户的 Windows 启动项中。

**正常语音完成后，会替换剪贴板中的原内容。**

## 兼容范围

| 项目 | 当前支持情况 |
| --- | --- |
| 工具运行位置 | Windows 宿主机，x64 |
| 微信输入法 | 2.1.4.6 / 3.0.0.34，且 `wetype_update.exe` 文件校验一致 |
| 输出 | Windows Unicode 文本剪贴板 |
| 远程使用 | 2.1.4.6 / 3.0.0.34 均在 Windows 通过向日葵控制 Mac 的场景验收；逐版本结果见验证记录 |
| 自动恢复 | 输入法重新启动后尝试重新连接；版本不匹配时停止读取 |
| macOS 原生运行 | 不支持；Mac 可以作为远程粘贴目标 |

这不是微信官方开放接口。输入法升级可能改变内部结构，需要重新适配。
已知版本信息和维护说明见 [兼容性说明](docs/compatibility.md)。

## 本地构建

在 Windows PowerShell 5.1 或 PowerShell 7 中运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-transcripts.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-clipboard.ps1 -DesktopClipboard
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-package.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-startup.ps1
```

这里的执行策略参数只作用于本次脚本进程，不修改系统设置。
构建使用 Windows 随 .NET Framework 提供的 C# 编译器，不下载依赖。
生成的单文件程序、ZIP 和校验文件位于 `dist`，不加入源码提交。
转录回放检查直接使用正式程序中的快照读取和轮次处理逻辑，覆盖两版内存结构、短字符串与中文长字符串、快照变化、无效数据、漏采停止状态、连续转录、取消、结果稳定、启动与恢复、对象变化以及剪贴板占用；不操作真实语音或剪贴板。
剪贴板检查调用正式写入器和真实通知接口，覆盖首次通知可读、一次完整通知、中文/长文本/表情/换行、连续相同文本、占用重试、无效文本及所有者退出后内容保留。它会短暂写入运行所在桌面的剪贴板，必须显式传入 `-DesktopClipboard`；原内容在内存中保存并恢复，检测到外部更新则保留新内容。运行时请暂停其他复制操作。CI 在专用 Windows 运行器上执行此检查。
开机启动检查使用独立的临时注册表键，覆盖默认关闭、启用和取消、设置保留、中文和空格路径、移动后更新及其他应用启动项不受影响；不会改动本机真实开机启动项。

本机已安装兼容输入法时，还可以进行只读连接验证：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-package.ps1 -LiveProbe
```

该验证不启动语音、不切换焦点、不写入剪贴板，只检查能否读取当前语音对象和状态。

## 发布

版本号保存在 `VERSION`。GitHub Actions 会在代码提交和拉取请求中构建并检查发布包。
维护者确认后推送与 `VERSION` 一致的版本标签，例如 `v0.2.4`，工作流才会创建 GitHub Release 并上传下载文件。
普通提交只构建和检查，不会创建 Release；版本标签的发布任务上传的就是同一轮 CI 已检查过的文件。

详见 [发布流程](docs/releasing.md) 和 [验证记录](docs/validation.md)。

## 许可证

[MIT](LICENSE)。
