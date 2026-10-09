# 应用图标

采用用户选定的 B 方案「语音复制」：蓝色叠层气泡与白色声波。

- `app-icon.png`：透明背景源图，由内置 imagegen 工具根据选定预览稿生成。
- `app.ico`：构建时生成的 Windows 图标，包含 16、20、24、32、40、48、64、128、256 像素帧。
- `../scripts/build-icons.ps1`：使用 Windows 自带的 System.Drawing 转换尺寸并封装 ICO，无外部构建依赖。

同一 ICO 用于 EXE 图标以及程序内嵌的托盘图标。托盘按当前系统小图标尺寸选择帧，退出时释放资源。
这不是微信官方标志。

生成提示词：

> Extract the single central blue icon from the selected option B concept card onto a genuinely transparent square canvas. Remove the title, presentation background and small examples. Preserve the blue front rounded speech card with a short bottom-left tail, the second rounded card offset behind it to the upper-right, and five solid white rounded waveform bars. Keep a transparent gap between the cards, vivid blue color, crisp rounded edges and even padding. No text, shadow, outer tile or extra symbols.
