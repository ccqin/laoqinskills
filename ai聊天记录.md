# UI Automation（UIA）框架详解

## 一、它是什么

UIA 是 Windows Vista 引入的系统级框架，设计上有**双重身份**：

1. **无障碍（Accessibility）**：屏幕阅读器、放大镜靠它读出界面内容，微软强制要求应用支持它
2. **自动化（Automation）**：测试工具、自动化脚本靠它“看见”并“操作”界面

这个双重身份正是它好用的根本原因：**每个正规控件为了无障碍合规，都必须把自己的结构、名称、能力暴露出来**——自动化测试白捡了一套现成的、跨框架的“界面 API”。

它取代了老的 MSAA（Active Accessibility）。Win32/WinForms/WPF/Qt/Electron/浏览器全都接入了这套体系，所以 UIA 是 Windows 桌面自动化的事实标准。

## 二、四个核心概念

### 1. 元素树（AutomationElement）

整个桌面是一棵树，根节点是 `RootElement`：

```
Desktop (Root)
└── 窗口 (Window, Name="PLC 测试工具集", ProcessId=15180)
    ├── Text   "PLC 测试工具集"          ← 标题
    ├── List   (导航栏)
    │   ├── ListItem "首页"
    │   ├── ListItem "Modbus 主站"
    │   └── ...
    └── Custom (内容区)
        ├── Button "连接"
        ├── Edit   (TextBox)
        └── DataGrid
            └── DataItem ...
```

有三种遍历视图：**Raw View**（原始树，全量）、**Control View**（去掉纯装饰元素）、**Content View**（只留有内容意义的元素，测试一般用这个）。

### 2. 属性（查元素就靠它）

每个元素带一组属性，常用的：

| 属性                                   | 用途                  | 测试建议                              |
| -------------------------------------- | --------------------- | ------------------------------------- |
| `AutomationId`                         | 程序员定的稳定 ID     | **查找首选**，不随语言/文案变         |
| `Name`                                 | 显示名（按钮文字等）  | 我们 `click.ps1` 就是按它找“连接”按钮 |
| `ControlType`                          | Button/Edit/ListItem… | 粗筛用                                |
| `ClassName` / `ProcessId`              | 类名/进程             | 定位某个进程的窗口                    |
| `IsEnabled` / `IsOffscreen` / `Bounds` | 状态与位置            | 断言用                                |

> **教训**：我们导航项最初按 "Modbus 主站" 找不到，因为 WPF 列表项的 `Name` 默认取 `ToString()`，显示成了类名——重写 `ToString()` 才修复。给控件显式设 `AutomationProperties.AutomationId` 是最稳的做法。

### 3. 控件模式（Pattern）——操作界面靠它

Pattern 是元素暴露的“能力接口”，相当于界面的 RPC：

| Pattern                               | 能干什么                       | 典型控件                    |
| ------------------------------------- | ------------------------------ | --------------------------- |
| `InvokePattern`                       | 点击                           | Button、MenuItem            |
| `ValuePattern`                        | 直接读写值（比模拟打字快且稳） | TextBox                     |
| `SelectionItemPattern`                | 选中/取消选中                  | ListItem、TabItem、CheckBox |
| `TogglePattern`                       | 切换开关                       | CheckBox                    |
| `ExpandCollapsePattern`               | 展开/折叠                      | TreeViewItem、ComboBox      |
| `RangeValuePattern`                   | 拖动数值                       | Slider                      |
| `ScrollPattern` / `ScrollItemPattern` | 滚动 / 滚到某项可见            | ScrollViewer、DataGrid 行   |
| `WindowPattern`                       | 关闭/最小化/模态等待           | Window                      |
| `GridPattern` / `TablePattern`        | 按行列取单元格                 | DataGrid                    |
| `TextPattern`                         | 读富文本内容                   | 文档控件                    |

用法是 `TryGetCurrentPattern` 试探（不是所有控件都有所有模式），我们脚本里就是这么写的。

### 4. 事件

不用轮询，可以订阅：焦点变化、属性变化（`NameProperty`、`ValueProperty`…）、树结构变化、窗口打开/关闭。测试里“等到某个元素出现/某个文字变成已连接”用它最优雅。

## 三、两种 API + 一个封装库

### 1. 托管包装 `System.Windows.Automation`（我们脚本用的）

```csharp
// 引用程序集：UIAutomationClient + UIAutomationTypes
var root = AutomationElement.RootElement;
var win = root.FindFirst(TreeScope.Children,
    new PropertyCondition(AutomationElement.NameProperty, "PLC 测试工具集"));

var btn = win.FindFirst(TreeScope.Descendants,
    new PropertyCondition(AutomationElement.NameProperty, "连接"));
((InvokePattern)btn.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
```

优点：零依赖、够用；缺点：微软已停止演进、性能一般（每次属性访问都跨进程）、查询能力弱。

### 2. 原生 COM `IUIAutomation`（官方主推）

`UIAutomationCore.dll` 里的 COM 接口，支持 CacheRequest（一次批量拉属性，大幅减少跨进程往返）、更全的模式。直接用比较繁琐。

### 3. **FlaUI**（做测试推荐这个）

NuGet 上的现代封装（`FlaUI.Core` + `FlaUI.UIA3`），把 COM UIA 包成流畅 API，还自带键盘/鼠标封装和重试工具，是当前 .NET 桌面 UI 测试的主流选择：

```csharp
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

using var automation = new UIA3Automation();
var app = FlaUI.Core.Application.Launch(@"D:\...\PlcToolset.App.exe");
var win = automation.GetDesktop()
    .FindFirstDescendant(cf => cf.ByProcessId(app.ProcessId))
    .AsWindow();

// 切到 Modbus 从站并点启动
win.FindFirstDescendant("Modbus 从站").AsListBoxItem().Select();
win.FindFirstDescendant(b => b.ByText("启动")).AsButton().Invoke();

// 等待状态徽标变化（内部自动重试）
var pill = win.WaitUntilClickable(cf => cf.ByText("监听中"), TimeSpan.FromSeconds(5));
```

## 四、做成自动化测试的工程骨架

结合 MSTest/xUnit + FlaUI，给我们的 PLC 工具集搭一个测试项目大概长这样：

```csharp
[TestClass]
public class ModbusLoopTests
{
    [TestMethod]
    public void 从站启动_主站连接_读取寄存器成功()
    {
        using var app = Application.Launch(AppPath);
        using var automation = new UIA3Automation();
        var win = automation.GetDesktop()
            .FindFirstDescendant(cf => cf.ByProcessId(app.ProcessId)).AsWindow();

        // 1. 从站页启动
        NavigateTo(win, "Modbus 从站");
        win.FindFirstDescendant(b => b.ByText("启动")).AsButton().Invoke();
        Assert.IsTrue(win.WaitUntilExists(cf => cf.ByText(x => x.StartsWith("TCP 监听")),
            TimeSpan.FromSeconds(5)) != null, "从站未进入监听状态");

        // 2. 主站页连接 + 读取
        NavigateTo(win, "Modbus 主站");
        win.FindFirstDescendant(b => b.ByText("连接")).AsButton().Invoke();
        win.FindFirstDescendant(b => b.ByText("读取")).AsButton().Invoke();

        // 3. 断言数据表出现数据行
        var grid = win.FindFirstDescendant(cf => cf.ByAutomationId("ManualGrid")).AsDataGridView();
        Assert.IsTrue(grid.Rows.Length > 0, "读取结果为空");
    }
}
```

## 五、必知的坑（都是这个项目里真实踩过的）

1. **虚拟化**：`DataGrid`/`TreeView` 开了 UI 虚拟化后，**没滚动到的行不在视觉树里，UIA 找不到**——当时树节点就是这个原因定位不到。解法：先 `ScrollIntoView`，或给容器设 `VirtualizingPanel.IsVirtualizing="False"`（仅测试场景）。
2. **懒加载容器**：WPF `TabControl` 只实例化激活页，非激活页的按钮对 UIA **不存在**。测试要先选 Tab 再找控件（我们的 `tab.ps1` 就是干这个的）。
3. **等待与重试**：UI 是异步的，`Find` 失败≠元素不存在，可能只是还没渲染。所有查找都要包“轮询 + 超时”，永远别裸 `Sleep`。
4. **权限边界（UIPI）**：普通权限进程**不能**自动化“以管理员运行”的窗口，反之可以。测试进程要与被测程序同权限级别。
5. **本地化**：按 `Name`（界面文字）查找会随语言变化，正式测试优先 `AutomationId`。
6. **UIA 不需要前台/焦点**：窗口可以被遮挡（这是它比“模拟鼠标键盘”强的关键）；但输入焦点类操作（真实键鼠模拟）仍会抢焦点。

## 六、工具箱

| 工具                                                     | 用途                                                         |
| -------------------------------------------------------- | ------------------------------------------------------------ |
| **Accessibility Insights for Windows**（微软官方，免费） | 查看任意窗口的 UIA 树/属性/模式——**开发测试前必备**，相当于 UI 界的 DevTools |
| Inspect.exe（Windows SDK 自带）                          | 同上，老牌                                                   |
| FlaUInspect                                              | 配合 FlaUI 查 AutomationId                                   |
| FlaUI                                                    | 测试库首选                                                   |
| ~~WinAppDriver~~                                         | 微软官方 Appium 驱动，**已停止维护**，不建议新项目用         |

**对比其他路子**：`SendKeys`/`SendInput` 是“盲打”（不看界面结构，靠坐标和焦点，极脆）；SikuliX 靠图像识别（更脆、慢）；UIA 是唯一“结构化、跨框架、不依赖前台”的方案。

---

如果你想动手，最快的起步是：装 Accessibility Insights 看一遍 PLC 工具集的元素树 → 给关键控件补上 `AutomationProperties.AutomationId` → 建 `tools/UiTests` 项目用 FlaUI 把三条自测闭环写成正式测试。需要的话我可以直接帮你把这个 UI 测试项目搭出来。