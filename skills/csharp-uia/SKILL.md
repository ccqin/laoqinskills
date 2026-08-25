---
name: csharp-uia
description: Read window/control content and operate desktop apps via UI Automation (C# .NET single-file app, run with dotnet run). Use this skill whenever the user wants to 读取运行中软件的窗口内容、获取界面控件/文本、导出控件树、查看窗口里的按钮/输入框文字、点击某个按钮、填写输入框、选择Tab/列表项、自动化操作桌面程序, dump or inspect a window's UI element tree, read control names/values/text, click a button, fill a text box, select a tab or list item, toggle a checkbox, send keystrokes to a control, wait for an element to appear, automate any running Windows desktop app (Win32/WinForms/WPF/Qt/Electron), even if they don't say "UIA". This tool never takes screenshots - pair it with csharp-screenshot for pixels.
---

# C# UIA 工具 (csharp-uia)

用 .NET 10 File-Based App 编写的 Windows UI Automation 工具：单文件 `scripts/uia.cs`，通过 `dotnet run` 直接运行，零 NuGet 依赖（引用系统自带 UIAutomationClient/UIAutomationTypes）。**只做"读 + 操作"，不做截图**——需要像素截图时配合 `csharp-screenshot`（见下文协作流程）。

## 用途

**核心用途：调试 Windows 桌面程序（Win32/WinForms/WPF/Qt/Electron），不需要源码和调试器。**

- **看程序当前真实的 UI 状态**：dump 控件树，读每个控件的名称、AutomationId、值、坐标和状态（选中/勾选/展开/禁用）。程序行为不对时，先看它界面实际长什么样、控件处于什么状态——比肉眼观察更准确（能看到隐藏值和状态）。
- **定位控件**：按名称/AutomationId/类名/控件类型找控件并拿到屏幕坐标，是后续一切操作和控件级截图的入口。
- **驱动程序复现问题**：模拟真实操作（点击、填值、选择、勾选、展开、发键）驱动程序走流程，替代手工点击，适合复现 bug、做自动化测试、批量重复操作。
- **盯状态变化**：`wait` 轮询等待某个文字/控件出现，判断程序是否完成了某步操作、是否弹出了预期提示。
- 与 `csharp-screenshot` 互补：本工具读**结构**（文本/值/状态/坐标），那个看**像素**（界面实际渲染成什么样）。

## 前提条件

- Windows 系统 + .NET SDK 10 或更高（`dotnet --version` 检查）
- 首次运行需编译约 5~15 秒，之后有构建缓存秒级启动
- 不确定目标窗口叫什么时，先 `--mode list` 列出所有可见顶层窗口

## 调用方式

```bash
dotnet run --file <本skill目录>/scripts/uia.cs -- [选项]
```

规则（与 csharp-screenshot 完全一致）：
- 始终带 `--file` 和 `--`（防止参数被 dotnet CLI 截获）
- 成功结果输出到 stdout（UTF-8）；错误到 stderr
- 退出码：0 成功、1 运行错误（找不到窗口/元素、超时）、2 参数错误

## 两种定位

**窗口定位（四选一，除 list 外必须给一个）：**
`--title <子串>`（忽略大小写）/ `--process <名>`（带不带 .exe 均可）/ `--pid <n>` / `--hwnd <n|0xHEX>`
加 `--any-window`：搜目标进程**全部**匹配顶层窗口而非第一个——弹层（ComboBox 下拉、ContextMenu、Popup）是独立 HWND，默认只搜主窗口时可能漏。

**元素定位（可组合，除 tree/wait 外操作类必须至少给一个）：**
`--name <子串>`（忽略大小写）/ `--id <AutomationId>`（最稳，精确）/ `--class <类名>` / `--control <类型>`（Button/Edit/ListItem/Text…） / `--index <n>`（默认第 1 个）

多个元素匹配时默认取第 1 个，并在 stderr 列出全部候选（含序号），用 `--index` 选择。**操作类模式（click/set/select/toggle/expand/scroll）会自动跳过不支持所需 pattern 的"幽灵匹配"**（如 WPF 弹层桥接进主窗口树的无 pattern 副本），在有 pattern 的真身中按 `--index` 取。

## 模式一览

| 模式 | 作用 | 关键参数 |
|---|---|---|
| `list` | 列出可见顶层窗口（标题/PID/进程/类名/hwnd/坐标） | 无需窗口定位 |
| `tree` | 导出目标窗口控件树（缩进文本或 JSON） | `--view content\|control\|raw`（默认 content）、`--depth`（默认30）、`--max-nodes`（默认4000）、`--format tree\|json`、`--out` |
| `find` | 搜索元素并平铺输出（**坐标入口**） | 元素定位参数 |
| `click` | Invoke 点击（无 Invoke 自动回退 Select/Toggle） | 元素定位参数；`--real` 坐标真实点击（见下） |
| `set` | 写文本（ValuePattern） | `--value <文本>` |
| `select` | 选中 Tab 项/列表项/下拉项（SelectionItem） | 元素定位参数 |
| `toggle` | 切换开关（Toggle） | 元素定位参数 |
| `expand` | 展开/折叠（ExpandCollapse） | `--collapse` 反向 |
| `keys` | 键盘输入（SetFocus + SendKeys，**会抢前台焦点**） | `--text "^a{DEL}abc"` |
| `wait` | 轮询等待窗口/元素出现 | `--timeout <秒>`（默认 3）；`--gone` 等**消失**；`--value <子串>` 只算 Value（无则 Name）含子串的匹配（等状态行变值） |
| `scroll` | 滚动进视图 | 不带 `--value`：对元素 ScrollIntoView；带 `--value <子串>`：元素定位指**容器**，容器内边滚边找 Name 含子串的项并滚进视图（**虚拟化列表**未滚到的行不在 UIA 树里，find 不到时用它） |
| `menu` | 右键元素 → 弹层中找菜单项 → Invoke（一次进程内完成，弹层稍纵即逝） | `--menu <菜单项文本>`；元素定位参数指向被右键的目标 |

**真实鼠标修饰（click/menu 专用）**：`--real` 坐标左键点击、`--right` 右键、`--double` 双击、`--hover` 悬停。移动用户鼠标、抢前台（目标被遮挡会先激活它）；用于无 pattern 的控件（幽灵弹层项、自绘按钮）或需要真实鼠标语义的场景。

所有查找默认内置 3 秒轮询重试（UI 是异步的，找不到≠不存在），可用 `--timeout` 调整。

## 常用示例

```bash
U="dotnet run --file <本skill目录>/scripts/uia.cs --"

# 看有哪些窗口
$U --mode list

# 导出某窗口整棵控件树（先看结构再操作）
$U --mode tree --process notepad
$U --mode tree --title "PLC" --format json --out tree.json

# 找"连接"按钮并拿坐标
$U --mode find --title "PLC" --name 连接
# → #1 Button "连接" ... rect=100,200 90x23 patterns=Invoke

# 点击它 / 填输入框 / 切Tab / 勾选
$U --mode click --title "PLC" --name 连接
$U --mode set --title "PLC" --id txtIp --value "192.168.0.1"
$U --mode select --title "PLC" --name "Modbus 主站"
$U --mode toggle --title "PLC" --name 自动重连

# 等状态文字出现（最多10秒）/ 等它消失 / 等它变成某值
$U --mode wait --title "PLC" --name "监听中" --timeout 10
$U --mode wait --title "PLC" --name "连接中" --gone
$U --mode wait --title "PLC" --id txtStatus --value "已连接"

# 虚拟化长列表:容器内滚动查找并把目标滚进视图
$U --mode scroll --title "PLC" --id lstLog --value "第999行"

# 右键树节点弹菜单点"重命名"(一次进程内完成)
$U --mode menu --title "PLC" --name 文档 --control TreeItem --menu 重命名

# WPF ComboBox 选下拉项:先展开再 select(幽灵副本会被自动跳过)
$U --mode expand --title "PLC" --id cmbColor
$U --mode select --title "PLC" --name 绿 --control ListItem

# 无 pattern 的控件用真实鼠标坐标点击 / 右键 / 双击 / 悬停
$U --mode click --title "PLC" --id btnCustom --real
```

## 与 csharp-screenshot 协作（控件级截图）

本工具输出的是文本和坐标（`rect=L,T WxH`，物理像素），截图交给 csharp-screenshot：

```bash
# 1) 用 uia 拿控件的屏幕坐标(输出含窗口 hwnd=0x…)
uia --mode find --process notepad --name 确定        # → rect=492,215 90x23, hwnd=0x80BFA
# 2) screenshot 用 hwnd(最稳,多窗口同标题不错位)+ region 窗口内裁剪(PrintWindow 渲染,被遮挡也能截准)
screenshot --hwnd 0x80BFA --region 492,215,90,23 --out ok-btn.png
```

## 注意事项

- **能读什么**：正规控件的 Name/AutomationId/值/状态/坐标。Win32/WinForms/WPF/Qt/Electron 都支持；UIA 操作（click/set/select）不需要窗口在前台。
- **读不到什么**：自绘内容（游戏/canvas/部分图表内部）、**虚拟化列表未滚动到的行**（用 `scroll --value` 容器滚动查找）、未激活 Tab 懒加载的控件（WPF 下先 `select` 切过去再找）。
- **WPF 弹层（下拉/ContextMenu）**：弹层是独立 HWND，内容会以"幽灵副本"桥接进主窗口树（**无任何 pattern**，直接操作报错）。操作类模式已自动跳过幽灵选真身；ComboBox 选下拉项用 `expand` + `select`；仍找不到加 `--any-window`；右键菜单直接用 `menu` 模式。
- **真实鼠标**（`--real/--right/--double/--hover`、`menu` 模式）**会移动用户鼠标并抢前台**；被遮挡的目标会先激活再点击；不要在用户操作电脑时使用。
- **WinForms 的 TabControl 页签头不暴露给 UIA**（无法用 select 切 WinForms 页签，WPF 可以）。
- **Chromium 系浏览器**（Chrome/Edge）默认不开 accessibility，页面内容读不到，需加启动参数 `--force-renderer-accessibility`。
- **权限（UIPI）**：普通权限进程不能读写"以管理员运行"的窗口，反之可以。
- `keys` 模式会 SetForegroundWindow 抢焦点，不要在用户操作时使用；其余模式不抢焦点。
- `keys` 受前台输入法干扰：中文 IME 开启时，英文字母会被组合成拼音候选汉字（实测发送 `abc123` 得到"按不出23"）。填文本优先用 `set`；`keys` 只用于快捷键，或先将系统输入法切为英文。
- 找元素优先用 `--id`（AutomationId 稳定不随语言变），其次 `--name`。

## 已验证

全部模式在 Win32/WinForms 真机测试通过（list/tree×3视图/find/set/click/select/toggle/expand/keys/wait/多匹配/越界/参数错误/退出码），并与 csharp-screenshot 完成控件级裁剪闭环验证。

WPF 真机复验通过（测试程序为仓库根 `testapp/WpfTestApp.cs`，覆盖 Button/TextBox/CheckBox/TabControl/ListBox(50项)/ComboBox/Expander/TreeView+ContextMenu）：list/tree(content/control/raw/json)/find(--id/--name/--control)/set/click/select(Tab页签+列表项+**ComboBox下拉项**)/toggle/expand(Expander+TreeViewItem，含 `--collapse`)/wait(**含 `--gone`/`--value`**)/scroll(**容器滚动查找虚拟化项**)/menu(**右键→点弹层菜单项**)/多匹配 `--index`/`--any-window`/无 InvokePattern 报错退出码 1/`click --real` 前台激活后坐标点击，uia find 拿坐标 + screenshot `--hwnd`+`--region` 控件级裁剪闭环亦通过。WPF 特有行为：未激活 Tab 页的控件不渲染，先 `select` 切页后才能 find；弹层幽灵副本自动跳过；`keys` 模式受中文输入法干扰（见注意事项）。
