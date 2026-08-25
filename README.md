# laoqinskills

个人 Agent Skills 集合（Windows 桌面自动化方向）。每个 skill 一个独立目录，结构统一为 `SKILL.md + scripts/`。

## 主入口（技能包路由）

遇到"想调试/操作/截图某个软件"类需求时，**先看 [`skills/laoqinskills/SKILL.md`](skills/laoqinskills/SKILL.md)**：它按"需求 → 用哪个 skill → 具体命令"路由，附标准调试工作流、控件级截图协作流程和注意事项速查。它本身也是一个纯引导型技能（无 scripts），随包一起安装后可在任意会话触发。

## Skill 清单

| Skill | 目录 | 职责 |
|---|---|---|
| **laoqinskills** | `skills/laoqinskills/` | 技能包主入口/路由（纯引导，无脚本） |
| **csharp-screenshot** | `skills/csharp-screenshot/` | 像素截图：全屏/显示器/区域/整窗/窗口内裁剪（PrintWindow 渲染，被遮挡也能截） |
| **csharp-uia** | `skills/csharp-uia/` | 结构化读写与操作：UIA 控件树导出、找控件拿坐标、点击/填值/选择/键盘、等待出现/消失/值变化、滚动查找、右键菜单 |

两者协作的典型流程（控件级截图）：

```bash
# uia 找控件拿坐标 → screenshot 按坐标在窗口内裁剪
uia --mode find --process notepad --name 确定        # → rect=492,215 90x23
screenshot --process notepad --region 492,215,90,23  # → 控件小图 PNG
```

## 开发约定

- 每个实现都是 **.NET 10 File-Based App 单文件 C#**：`dotnet run --file xxx.cs -- <args>`
- 统一接口约定：成功结果到 stdout、错误到 stderr；退出码 0 成功 / 1 运行错误 / 2 参数错误；`--help` 查看用法
- 新 skill 统一放 `skills/<skill-name>/`：`skills/<skill-name>/SKILL.md`（YAML frontmatter：name + 双语 description 触发词）+ `skills/<skill-name>/scripts/*.cs`

## 同步到全局安装

开发/修改完成后，把 skill 目录复制到全局 skills 目录使其在任意会话可被触发：

```bash
cp -r skills/laoqinskills skills/csharp-uia skills/csharp-screenshot /c/Users/ccqin/.agents/skills/
```

> 备份：旧版截图 skill 保留在 `D:\15.ai\截图Skills`（含历史 .zcode 会话数据）。
