# 验证记录

日期：2026-10-09。

## v0.1.1 图标更新验证

- 使用用户选定的蓝色叠层语音气泡图标，嵌入 EXE 和托盘资源。
- 本地构建、文件版本、校验和、ZIP 内容以及真实微信输入法只读连接验证通过。
- 用 Windows/.NET Framework 实际加载并绘制内嵌图标，确认 16 至 128 像素的八种托盘尺寸均可加载、绘制且保留透明背景；该检查已加入发行包验证流程。ICO 另含供资源管理器使用的 256 像素 PNG 帧。
- 在浅色、深色背景下检查了 16 至 128 像素显示效果，也从 EXE 提取图标确认程序文件图标一致。
- 本次语音读取与复制逻辑没有改动，未重新要求用户完成一轮语音与远程粘贴测试。

## 原始功能验证

在 Windows 宿主机上运行微信输入法 2.1.4.6，并通过向日葵控制 Mac：

- 用户自行使用 Alt+Q 开始、结束语音。
- 后台捕获程序读取最终文字并写入宿主机剪贴板。
- 用户确认在远程 Mac 用 Ctrl+V 能正确粘贴新识别的内容。
- 常驻版本也经用户确认正常，取消操作最终由用户确认可以正常取消。
- 常驻版本曾自动复制一段 240 字的识别结果；宿主机剪贴板文字长度一致。
- 重复启动只保留一个后台实例。

这些是特定环境中的实测结果，不代表已经覆盖所有软件、版本或异常情况。
源码迁入本仓库时，`src/WeTypeVoiceCapture.cs` 与该常驻版本源码逐字节一致。
项目化构建另加了版本信息和以普通用户权限运行的应用清单。

## v0.1.0 本地发行包验证

执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\test-package.ps1 -LiveProbe
```

结果：

- 编译成功，生成带有 `0.1.0.0` 文件版本的 Windows x64 图形程序。
- 单文件 EXE 和 ZIP 的 SHA-256 均匹配随包校验记录。
- ZIP 内仅含程序、使用说明、许可证。
- ZIP 解压后的 EXE 与单文件下载 EXE 完全一致。
- 从带空格的解压目录启动只读诊断，成功定位当前微信输入法的语音对象。
- 此次包验证没有切换焦点、启动语音或写入剪贴板。

## GitHub Actions 与正式下载验证

- [首次 main 自动构建](https://github.com/lzhshen/wetype-voice-clipboard/actions/runs/37905370287)：通过。编译、发行包校验、解压启动检查及构建产物上传均成功。
- [v0.1.0 自动构建与发布](https://github.com/lzhshen/wetype-voice-clipboard/actions/runs/37905530837)：通过。标签与 `VERSION` 匹配，构建任务和 Release 发布任务均成功。
- [正式 Release](https://github.com/lzhshen/wetype-voice-clipboard/releases/tag/v0.1.0)：包含单文件 EXE、ZIP 和 `SHA256SUMS.txt` 三个公开下载文件。

GitHub 托管运行器没有安装真实微信输入法，因此 CI 检查的是包结构及诊断程序可启动，并明确报告未在该环境执行真实输入法连接测试。

发布后，另外通过公开下载链接、不带认证信息下载三个 Release 文件：

1. 下载文件的 SHA-256 与 GitHub Release 元数据中的文件摘要一致。
2. 单文件 EXE 和 ZIP 均通过随 Release 提供的校验记录。
3. ZIP 解压后的程序与单文件下载完全一致，文件版本为 `0.1.0.0`，架构为 Windows x64。
4. 对下载回来的程序执行 `test-package.ps1 -LiveProbe`，在真实输入法环境成功读取语音对象。
5. 下载版验证没有开始或结束语音、切换焦点或写入剪贴板。

正式下载文件的 SHA-256：

| 文件 | SHA-256 |
| --- | --- |
| `WeTypeVoiceCapture-v0.1.0-win-x64.exe` | `3c591b4ddb62439066dd1fc0e7a2a86395e64fca167da423ac1132979d06100c` |
| `wetype-voice-clipboard-v0.1.0-win-x64.zip` | `c1d6eca86f1953aeb362f7b6117e7591fc16229045f4e5cb2d62052cd4b6c914` |

发布文件由 CI 编译；其字节校验值无需与此前单独进行的本地编译一致。以上校验值对应实际公开发布的文件。
