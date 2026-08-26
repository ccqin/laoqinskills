# laoqinskills

个人 Agent Skill：**Windows 桌面自动化调试工具包**（单技能包，下载解压即用）。

## 结构

```
laoqinskills/
├── SKILL.md              # 技能入口:需求→工具路由表 + 标准调试工作流 + 注意事项速查
├── docs/
│   ├── uia.md            # uia 详细用法(读结构与操作)
│   └── screenshot.md     # screenshot 详细用法(像素截图)
├── scripts/
│   ├── uia.cs            # UIA:控件树导出、找控件拿坐标、点击/填值/选择/键盘、等待、滚动查找、右键菜单
│   └── screenshot.cs     # 像素截图:全屏/显示器/区域/整窗/窗口内控件裁剪(PrintWindow,被遮挡也能截)
└── testapp/WpfTestApp.cs # WPF 测试程序(开发验证用,不随技能安装)
```

## 安装（解压即用）

从 [GitHub Releases](https://github.com/ccqin/laoqinskills/releases) 下载 zip，解压出 `laoqinskills` 文件夹，整个放进技能目录即完成安装：

```
C:/Users/ccqin/.agents/skills/laoqinskills/   ← 解压后的文件夹放这里
    ├── SKILL.md
    ├── docs/
    └── scripts/
```

本机开发仓库同步（效果相同）：

```bash
mkdir -p /c/Users/ccqin/.agents/skills/laoqinskills
cp -r SKILL.md docs scripts /c/Users/ccqin/.agents/skills/laoqinskills/
```

## 两个工具的分工

| 维度 | uia（scripts/uia.cs） | screenshot（scripts/screenshot.cs） |
|---|---|---|
| 角色 | 读**结构** + 模拟操作 | 看**像素** |
| 能得到 | 控件树、名称/AutomationId、值、状态、物理像素坐标 | PNG/JPEG/BMP 图片文件 |
| 被其他窗口遮挡 | 可读可操作 | 可截（PrintWindow 渲染） |
| 典型场景 | 调试 Windows 程序：看 UI 真实状态、驱动复现、盯状态变化 | 留证 UI 现场、看渲染效果、截控件特写 |

两者协作的典型流程（控件级截图）：

```bash
# uia 找控件拿坐标(输出含 hwnd=0x…) → screenshot 用 hwnd+region 在窗口内裁剪
uia        --mode find --process notepad --name 确定        # → rect=492,215 90x23, hwnd=0x80BFA
screenshot --hwnd 0x80BFA --region 492,215,90,23 --out ok-btn.png
```

## 开发约定

- 每个实现都是 **.NET 10 File-Based App 单文件 C#**：`dotnet run --file xxx.cs -- <args>`
- 统一接口约定：成功结果到 stdout、错误到 stderr；退出码 0 成功 / 1 运行错误 / 2 参数错误；`--help` 查看用法
- 详细用法文档放 `docs/`，新增功能同步更新 SKILL.md 路由表和对应 docs 文档
- 修改后：同步全局（上方 cp 命令）+ 提交推送；`git tag vX.Y.Z && git push origin vX.Y.Z` 自动发 Release（zip 打包 SKILL.md/README/docs/scripts，解压即用）

> 备份：旧版截图 skill 保留在 `D:\15.ai\截图Skills`（含历史 .zcode 会话数据）。
