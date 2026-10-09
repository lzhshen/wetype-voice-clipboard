# 验证记录

日期：2026-10-09。

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

GitHub Actions 的首次远程构建与 Release 下载验证，需要在所有者确认、首次提交和发布之后执行；当前记录不将其算作已完成。
