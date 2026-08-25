---
name: laoqinskills
description: Windows desktop automation skill pack main entry / router. Use when the user wants to 调试 Windows 程序、桌面自动化、读取或操作运行中软件的界面、给程序截图或截控件图, and the right sub-skill is not obvious. This file routes the need to csharp-uia (read & operate window/control content, debug Windows programs) or csharp-screenshot (pixel screenshots: full screen / monitor / region / window / control crop). 调试桌面软件、看程序界面里有什么、点按钮填表单、截屏截窗口截控件, start here.
---

# laoqinskills 技能包(主入口)

Windows 桌面自动化技能包的引导文件：**先在这里确定"要做的事"归哪个技能、用什么模式，再去对应 skill 目录执行。**

## 两个技能的分工

| 维度 | csharp-uia | csharp-screenshot |
|---|---|---|
| 角色 | 读**结构** + 模拟操作 | 看**像素** |
| 能得到 | 控件树、名称/AutomationId、值、状态、物理像素坐标 | PNG/JPEG/BMP 图片文件 |
| 被其他窗口遮挡 | 可读可操作 | 可截（PrintWindow 渲染） |
| 窗口最小化 | 仍可读 | 截图会报错，需先还原 |
| 典型场景 | 调试 Windows 程序：看 UI 真实状态、驱动复现、盯状态变化 | 留证 UI 现场、看渲染效果、截控件特写 |

## 怎么调用

两个技能都是 .NET 10 File-Based App 单文件脚本，位于本仓库 `skills/` 下（全局安装后平铺到 `C:/Users/ccqin/.agents/skills/`）。命令形态：

```bash
# 仓库内：
uia        = dotnet run --file <包根>/skills/csharp-uia/scripts/uia.cs --
screenshot = dotnet run --file <包根>/skills/csharp-screenshot/scripts/screenshot.cs --
# 全局安装后：
uia        = dotnet run --file C:/Users/ccqin/.agents/skills/csharp-uia/scripts/uia.cs --
screenshot = dotnet run --file C:/Users/ccqin/.agents/skills/csharp-screenshot/scripts/screenshot.cs --
```

- 始终带 `--file` 和 `--`；首次运行编译 5~15 秒，之后秒级
- 成功输出到 stdout（screenshot 输出保存文件的绝对路径），错误到 stderr
- 退出码：0 成功 / 1 运行错误 / 2 参数错误；`--help` 看用法
- 两者窗口定位一致，四选一：`--title <子串>` / `--process <名>` / `--pid <n>` / `--hwnd <n|0xHEX>`

## 需求 → 技能路由表

| 我想… | 调用 | 命令示例 |
|---|---|---|
| 看系统里有哪些窗口可选目标 | uia `list` | `uia --mode list` |
| 看某程序界面里有什么控件、值、状态 | uia `tree` | `uia --mode tree --process plc`（`--view raw` 看模板部件，`--format json --out t.json` 导 JSON） |
| 找某按钮/输入框在哪、拿坐标 | uia `find` | `uia --mode find --process plc --name 连接` → `rect=100,200 90x23` |
| 点按钮/触发动作 | uia `click` | `uia --mode click --process plc --name 连接` |
| 往输入框填文本（**优先于 keys**） | uia `set` | `uia --mode set --process plc --id txtIp --value "192.168.0.1"` |
| 发快捷键/组合键 | uia `keys` | `uia --mode keys --process plc --id txtIp --text "^a{DEL}abc"`（抢前台焦点，见注意事项） |
| 切 Tab 页 / 选列表项 | uia `select` | `uia --mode select --process plc --name "高级"` |
| 勾选/取消复选框 | uia `toggle` | `uia --mode toggle --process plc --name 自动重连` |
| 展开/折叠树节点或 Expander | uia `expand` | `uia --mode expand --process plc --name 文档`（反向加 `--collapse`） |
| 等程序出现某文字/控件（判断流程走到哪步） | uia `wait` | `uia --mode wait --process plc --name 监听中 --timeout 10` |
| 截全屏（多显示器合影） | screenshot `full` | `screenshot --mode full` |
| 截某个显示器 | screenshot `monitor` | `screenshot --mode monitor --index 1` |
| 截屏幕某区域 | screenshot `region` | `screenshot --mode region --region 0,0,1920,1080` |
| 截某程序整窗（**被遮挡也准**） | screenshot `window` | `screenshot --process plc` |
| **截某个控件的特写小图** | uia `find` → screenshot | 两步，见下文协作流程 |
| 留证报错弹窗/异常界面 | screenshot `window` | `screenshot --title 错误 --out err.png` |
| 指定格式/延迟/光标 | screenshot 附加参数 | `--format jpeg --quality 85 --delay 2 --cursor` |

## 标准工作流：调试一个陌生的 Windows 程序

```bash
# 1) 找到目标窗口(记下标题/进程/hwnd)
uia --mode list

# 2) dump 控件树,了解结构和 AutomationId(比肉眼看截图信息多:有值和状态)
uia --mode tree --process 目标进程

# 3) 需要看界面实际渲染效果时,整窗截图(被遮挡也能截)
screenshot --process 目标进程

# 4) 定位到具体控件,驱动操作复现问题
uia --mode find  --process 目标进程 --name 连接      # 先拿坐标确认找对
uia --mode click --process 目标进程 --name 连接
uia --mode set   --process 目标进程 --id txtIp --value "192.168.0.1"

# 5) 验证结果:等状态文字出现 / 重读控件值 / 截图对比
uia --mode wait --process 目标进程 --name 监听中 --timeout 10
uia --mode tree --process 目标进程
screenshot  --process 目标进程 --out after.png
```

## 控件级截图(两技能协作)

uia 只输出文本和坐标，截图交给 screenshot；窗口经 PrintWindow 渲染后裁剪，被遮挡也准：

```bash
# 1) uia 拿控件的屏幕物理像素矩形
uia --mode find --process notepad --name 确定
#   → #1 Button "确定" ... rect=492,215 90x23 patterns=Invoke
# 2) 同窗口定位 + rect 裁出控件小图
screenshot --process notepad --region 492,215,90,23 --out ok-btn.png
```

## 注意事项速查（详细版见各 skill 的 SKILL.md）

- **元素定位优先 `--id`**（AutomationId 稳定不随语言变），其次 `--name`；多匹配时默认取第 1 个，stderr 列出全部候选，用 `--index k` 选。
- **填文本优先 `set`**；`keys` 会抢前台焦点，且中文输入法开启时字母会被组合成汉字（实测 `abc123` → "按不出23"），`keys` 只用于快捷键或先切英文输入法。
- **未激活 Tab 页的控件不存在**：WPF 先 `select` 切页再 find；WinForms 页签头不暴露给 UIA，无法 select。
- **窗口最小化**：uia 仍可读，screenshot 会报错，先还原窗口。
- **坐标一律物理像素**；多显示器时主屏左侧/上方为负值，`--mode list` 可确认布局。
- **Chromium 系浏览器**（Chrome/Edge）默认页面内容读不到，需加启动参数 `--force-renderer-accessibility`。
- **权限（UIPI）**：普通权限进程不能读写"以管理员运行"的窗口，反之可以。
- 截不到：DRM 保护内容、独占全屏 DirectX 游戏、自绘内容（游戏/canvas 内部）。
- WPF 程序可用的测试目标：`testapp/WpfTestApp.cs`（`dotnet run --file` 直接跑）。
