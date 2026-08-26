# screenshot 详细用法(像素截图)

> laoqinskills 技能包中 screenshot 工具(`../scripts/screenshot.cs`)的完整参考;需求路由与速查见[../SKILL.md](../SKILL.md)。

用 .NET 10 File-Based App 编写的 Windows 截图工具:单文件 `scripts/screenshot.cs`,通过 `dotnet run` 直接运行,仅一个 NuGet 依赖(System.Drawing.Common)。用 GDI BitBlt 抓屏并已启用 Per-Monitor V2 DPI 感知,高分屏不模糊、多显示器坐标准确。

## 用途

**核心用途：留存和查看界面当前实际渲染的像素内容。**

- **看界面上实际显示了什么**：全屏 / 指定显示器 / 任意区域 / 某个程序的整窗口。目标窗口被其他窗口遮挡也能截准（PrintWindow 渲染），适合截取 Agent 终端背后的软件。
- **截单个控件的小图**：配合 `uia` 拿到控件坐标后裁剪出按钮/输入框/图表区域的特写（见下文协作流程）。
- **留存 UI 现场证据**：报错弹窗、异常界面、操作前后的对比，保存为 PNG/JPEG/BMP 文件供事后查看或发给他人。
- 与 `uia` 互补：本工具看**像素**（渲染结果），那个读**结构**（控件树/值/状态）。

## 前提条件

- Windows 系统 + .NET SDK 10 或更高(用 `dotnet --version` 检查)
- 首次运行需要还原 NuGet 包并编译,约 5~15 秒;之后有构建缓存,秒级启动
- 若不确定显示器布局或有哪些窗口,先执行 `--mode list` 查看

## 调用方式

```bash
dotnet run --file <本技能包>/scripts/screenshot.cs -- [选项]
```

规则:
- 始终带 `--file`(强制文件模式)和 `--`(把后面参数传给程序,防止被 dotnet CLI 截获)
- `<本技能包>` 替换为本 Skill 的绝对路径,例如 `C:/Users/ccqin/.agents/skills/screenshot`
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
| **窗口内裁剪某控件**(配合 uia) | `--hwnd 0x80BFA --region 492,215,90,23`(hwnd 来自 uia find 输出) |
| 指定输出文件 | `--out D:/shots/demo.png`(或简写 `-o`) |
| JPEG + 质量 | `--format jpeg --quality 80` |
| 截图前延迟 + 带鼠标光标 | `--delay 1.5 --cursor` |

组合示例(延迟 2 秒后截取区域,存为 JPEG):

```bash
dotnet run --file C:/Users/ccqin/.agents/skills/laoqinskills/scripts/screenshot.cs -- --mode region --region 0,0,1920,1080 --delay 2 --format jpeg --quality 85 --out D:/shots/region.jpg
```

## 控件级截图:与 uia 协作

本工具只认像素坐标。要截"某个按钮/控件"时,先用 uia 拿到控件的屏幕坐标和窗口 hwnd,再用 **`--hwnd` + `--region`** 裁剪(窗口经 PrintWindow 渲染后裁剪,**目标被其他窗口遮挡也能截准**;hwnd 精确锁定窗口,多窗口同标题时不会错位):

```bash
# 1) uia 找控件,输出 rect=左,上 宽x高 和窗口 hwnd
dotnet run --file C:/Users/ccqin/.agents/skills/laoqinskills/scripts/uia.cs -- --mode find --process notepad --name 确定
#   → window: "…" hwnd=0x80BFA
#     #1 Button "确定" ... rect=492,215 90x23 patterns=Invoke
# 2) 用该 hwnd + rect 截出控件小图
dotnet run --file C:/Users/ccqin/.agents/skills/laoqinskills/scripts/screenshot.cs -- --hwnd 0x80BFA --region 492,215,90,23
```

窗口图按 **DWM 可见边框**(EXTENDED_FRAME_BOUNDS)对齐,与 uia 控件坐标同一网格:先按 GetWindowRect 渲染再裁掉约 5~7px 不可见缩放边框,控件裁剪无系统性偏移(已用标准边框窗口实测核验)。

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
- `--mode list` 现在同时列出显示器和所有可见顶层窗口(标题/PID/进程/类名/hwnd/坐标),与 uia 的 `--mode list` 对齐,方便选目标。
- `--title`/`--process`/`--pid` 查找已过滤 cloaked 幽灵窗口(UWP 挂起副本),与 uia 的窗口解析一致;要绝对精确时用 `--hwnd`。
- 无法捕获 DRM 保护内容和独占全屏的 DirectX 游戏(这类需求需要 Windows Graphics Capture,超出本工具范围)。
- 截完图后可以直接用 Read 工具查看生成的 PNG 以确认内容。
