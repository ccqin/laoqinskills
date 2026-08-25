---
name: csharp-screenshot
description: Take screenshots on Windows via a C# .NET file-based app (single .cs file run with dotnet run). Use this skill whenever the user wants to 截图、截屏、屏幕截图、区域截图、窗口截图、截取屏幕/桌面/窗口内容、按进程名或窗口标题截取某个程序、截取某个控件/按钮的截图, take a screenshot, capture the screen, capture a region / monitor / window by title, process name, pid or handle, crop a control-level screenshot inside a window using a screen-absolute rect, grab what is on screen, or save screen content to a PNG/JPEG/BMP image, even if they don't explicitly say "screenshot". Window capture works even when the target window is occluded by other windows. Supports full virtual screen across all monitors, a specific region, a specific monitor, cropping a rect inside a rendered window, plus delay, mouse cursor overlay, and JPEG quality control. For control coordinates pair with csharp-uia.
---

# C# 截图工具 (csharp-screenshot)

用 .NET 10 File-Based App 编写的 Windows 截图工具:单文件 `scripts/screenshot.cs`,通过 `dotnet run` 直接运行,仅一个 NuGet 依赖(System.Drawing.Common)。用 GDI BitBlt 抓屏并已启用 Per-Monitor V2 DPI 感知,高分屏不模糊、多显示器坐标准确。

## 前提条件

- Windows 系统 + .NET SDK 10 或更高(用 `dotnet --version` 检查)
- 首次运行需要还原 NuGet 包并编译,约 5~15 秒;之后有构建缓存,秒级启动
- 若不确定显示器布局或有哪些窗口,先执行 `--mode list` 查看

## 调用方式

```bash
dotnet run --file <本skill目录>/scripts/screenshot.cs -- [选项]
```

规则:
- 始终带 `--file`(强制文件模式)和 `--`(把后面参数传给程序,防止被 dotnet CLI 截获)
- `<本skill目录>` 替换为本 Skill 的绝对路径,例如 `C:/Users/ccqin/.agents/skills/csharp-screenshot`
- 成功时 stdout 输出**保存文件的绝对路径**,直接解析该行即可
- 失败信息输出到 stderr;退出码:0 成功、1 运行错误、2 参数错误

## 常用示例

| 场景 | 命令 |
|---|---|
| 全屏(所有显示器合影) | `--mode full` |
| 列出显示器布局 + 可见窗口清单 | `--mode list` |
| 指定区域(屏幕级) | `--mode region --region 100,100,800,600` |
| 指定显示器 | `--mode monitor --index 1` |
| 按窗口标题截窗口 | `--mode window --title "记事本"` |
| 按进程名截窗口(推荐,可省略 --mode window) | `--process notepad` 或 `--process notepad.exe` |
| 按进程 PID 或窗口句柄截窗口 | `--pid 1234` / `--hwnd 0x80582`(句柄支持十进制和 0x 十六进制) |
| **窗口内裁剪某控件**(配合 csharp-uia) | `--process notepad --region 492,215,90,23` |
| 指定输出文件 | `--out D:/shots/demo.png`(或简写 `-o`) |
| JPEG + 质量 | `--format jpeg --quality 80` |
| 截图前延迟 + 带鼠标光标 | `--delay 1.5 --cursor` |

组合示例(延迟 2 秒后截取区域,存为 JPEG):

```bash
dotnet run --file C:/Users/ccqin/.agents/skills/csharp-screenshot/scripts/screenshot.cs -- --mode region --region 0,0,1920,1080 --delay 2 --format jpeg --quality 85 --out D:/shots/region.jpg
```

## 控件级截图:与 csharp-uia 协作

本工具只认像素坐标。要截"某个按钮/控件"时,先用 csharp-uia 拿到控件的屏幕坐标,再把坐标作为 `--region` 与窗口定位参数组合(窗口经 PrintWindow 渲染后裁剪,**目标被其他窗口遮挡也能截准**):

```bash
# 1) csharp-uia 找控件,输出 rect=左,上 宽x高
dotnet run --file C:/Users/ccqin/.agents/skills/csharp-uia/scripts/uia.cs -- --mode find --process notepad --name 确定
#   → #1 Button "确定" ... rect=492,215 90x23 patterns=Invoke
# 2) 用同窗口定位 + rect 截出控件小图
dotnet run --file C:/Users/ccqin/.agents/skills/csharp-screenshot/scripts/screenshot.cs -- --process notepad --region 492,215,90,23
```

## 参数一览

| 参数 | 说明 |
|---|---|
| `--mode <full\|monitor\|region\|window\|list>` | 截图模式,默认 `full` |
| `--index, --monitor <n>` | 显示器编号(monitor 模式,默认 0) |
| `--region <x,y,w,h>` | 物理像素区域。单独使用=屏幕级区域(region 模式);**与窗口定位参数组合=在渲染后的窗口内裁剪** |
| `--title <子串>` | 窗口标题子串,不区分大小写(window 模式) |
| `--process, --pname <名称>` | 进程名,带不带 .exe 均可(如 notepad),自动选主窗口 |
| `--pid <n>` | 进程 ID,定位该进程的主窗口 |
| `--hwnd <句柄>` | 窗口句柄,十进制或 0x 十六进制 |
| `--out, -o <路径>` | 输出文件,默认 `screenshot-<时间戳>.png` 存当前目录 |
| `--format, -f <png\|jpeg\|bmp>` | 格式;默认取 `--out` 扩展名,否则 png |
| `--quality, -q <1-100>` | JPEG 质量,默认 90 |
| `--delay <秒>` | 截图前等待,支持小数如 `1.5` |
| `--cursor` | 把鼠标光标画进截图 |
| `--help, -h` | 帮助 |

## 注意事项

- 坐标一律是**物理像素**;多显示器时主屏左侧/上方的屏幕坐标为**负值**,可用 `--mode list` 确认布局。区域会自动钳制到虚拟屏幕/窗口范围内。
- `--mode window` 用 PrintWindow 渲染,即使目标窗口被其他窗口遮挡也能正确截取(适合截取 Agent 终端背后的窗口);窗口最小化时会报错,需先还原。
- 窗口定位四选一、互斥:`--title` / `--process` / `--pid` / `--hwnd`。提供任一窗口定位参数时可省略 `--mode window`(自动切换);同时给 `--region` 则做窗口内裁剪。
- `--mode list` 现在同时列出显示器和所有可见顶层窗口(标题/PID/进程/类名/hwnd/坐标),与 csharp-uia 的 `--mode list` 对齐,方便选目标。
- 无法捕获 DRM 保护内容和独占全屏的 DirectX 游戏(这类需求需要 Windows Graphics Capture,超出本工具范围)。
- 截完图后可以直接用 Read 工具查看生成的 PNG 以确认内容。
