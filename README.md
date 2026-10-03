# iDevelop

用于协作开发多 agent 图形界面的项目。目前完成的是开发环境；产品需求、界面和技术栈尚未设计。

本项目使用 [Lauren Tan（@poteto）的 PStack](https://github.com/cursor/plugins/tree/main/pstack)，版本 `0.15.6`，固定提交 `23e4138daa01c42d4969f7a5465f82704e64f798`。49 个 skills、24 个原则和 23 个 playbooks 来自官方源。这里增加的是项目级跨客户端适配，不是官方的 Codex/Pi/Claude/Gemini 移植版本。

## 初始化

需要 Node.js 22 或更新版本、Git，以及你要使用的 agent 客户端。没有应用依赖需要安装。

```powershell
git clone https://github.com/Mano-Liaoyan/iDevelop.git
cd iDevelop
node scripts/pstack.mjs setup
node scripts/pstack.mjs check
```

初始化会自动下载主仓库记录的 PStack submodule 版本，在项目里生成 `.agents/skills`。Claude 和 Cursor 的技能目录链接到它，Gemini 原生识别它，Pi 启动入口显式加载它。重新克隆、创建新 worktree 后运行初始化。首次初始化需要网络；生成目录和个人设置不提交。

上游放在 `.pstack/upstream`，主仓库只记录官方仓库地址和提交版本。官方 PStack 位于 `cursor/plugins` 多插件仓库中，因此 submodule 指向整个仓库；初始化通过 sparse checkout 只展开 `pstack` 及上游根目录文件，不展开其他插件目录。源码位于 `.pstack/upstream/pstack`，许可证也保留在其中。

这里选择 submodule 而非 subtree。Subtree 会继续把上游文件作为主仓库普通文件提交；submodule 让项目提交和代码审查只显示一个版本指针。它不会安装全局插件，也不会从已有 Git 历史中删除旧的源文件快照。

## 在 Codex 桌面端调用 skills

在输入框的 `@` 菜单搜索 `poteto-mode`，或直接在消息中写 `$poteto-mode`。例如：

```text
$poteto-mode 检查这个项目的开发环境，并说明下一步需要做什么。
```

PStack 上游文档中的 `/poteto-mode` 是 Cursor 的调用写法，不能用 Codex 的斜杠菜单判断技能是否安装。Codex 已在 2026 年 3 月将 skills 加入 [`@` 菜单](https://learn.chatgpt.com/docs/changelog)。本项目的 49 个 PStack skills 已通过实际发现接口验证；若初始化前就打开了会话，可在此项目新开会话后重新搜索。

能调用项目技能与隐藏其他技能是两件事。下面的表格说明桌面端当前的隔离限制；没有为隐藏它们而修改全局技能配置。

## 启动客户端

在 Windows PowerShell 中使用项目入口，避免普通启动命令重新加载全局技能。

```powershell
.\scripts\agent.ps1 codex
.\scripts\agent.ps1 claude
.\scripts\agent.ps1 pi
.\scripts\agent.ps1 gemini
```

脚本不安装客户端，不复制凭据，不更改其他项目的配置。启动参数可附在客户端名之后。技能过滤依赖客户端版本和组织策略；启动时不要用参数重新引入其他插件或技能源。

| 客户端 | 项目隔离方式 | 验证和限制 |
| --- | --- | --- |
| Codex CLI | 启动入口按实际 `SKILL.md` 路径生成会话级 `-c skills.config=...` 参数；保留工具连接 | `node scripts/pstack.mjs isolate-codex` 生成过滤，`audit-codex` 用同一参数调用真实 `skills/list`，检查启用项恰好是 49 个 PStack skills，并检查普通父目录会话仍可使用其他 skills。实际模型提示词也已验证只含这 49 项。 |
| Codex 桌面版 | 读取项目 PStack 和 `AGENTS.md` | 当前 `0.160.0` 忽略项目层的 `skills.config` 过滤；未找到受支持的桌面项目级技能隐藏方式。普通桌面聊天仍可能显示其他 skills。必须使用上述 CLI 入口才能得到已验证的严格隔离。 |
| Claude Code | `--setting-sources project,local` 排除个人技能和同步来源；关闭 bundled skills，隐藏 doctor | 共享 `.claude/skills`。组织管理配置仍可能生效。安装客户端后核实 `/skills` 和 `/plugin`。 |
| Pi | `--no-extensions --no-skills --skill <项目目录> --no-prompt-templates` | 仅显式加载 PStack。关闭全局 extensions 也会关闭由这些 extensions 提供的子 agent/MCP 功能；后续如需它们，先审核再显式加载项目内扩展。普通 `pi` 不保证隔离。 |
| Gemini CLI | 独立的项目内 `GEMINI_CLI_HOME`；启动时枚举并禁用非 PStack skills，再检查启用列表 | 需要在此独立 profile 登录并认可项目。管理列表可能仍显示 Disabled 的内置技能；官方没有完整的“只显示白名单”开关。列表输出格式改变时入口会停止，而不是假装验证通过。 |

Gemini 首次登录/信任设置需要进入它的原生界面。在新开的 PowerShell 窗口中，从本项目目录执行以下命令，完成界面提示后退出。此首次引导尚未执行技能过滤，不用于开发。关闭窗口即可恢复普通环境；之后使用项目入口。

```powershell
$env:GEMINI_CLI_HOME = Join-Path (Get-Location) '.pstack/runtime/gemini-home'
gemini
```

Codex 入口每次启动前刷新过滤并验证；直接运行普通 `codex` 不会携带这些参数。路径过滤是当前安装的快照，不是未来所有插件的通配禁令。客户端的技能管理页面可能仍列出 disabled 项；验证的严格条件是会话中启用的技能仅来自 PStack。本机仅 Codex 已实测，Claude、Pi、Gemini 的配置按官方文档准备，尚未进行真实客户端验收。

## 工作流与共享记录

工程任务默认遵循 PStack。给出目标、约束和可验证的完成条件即可，`poteto-mode` 负责选择 playbook。也可以明确调用：Codex `$poteto-mode`，Claude `/poteto-mode`，Pi `/skill:poteto-mode`；Gemini 可要求“使用 poteto-mode skill”。

- [`AGENTS.md`](AGENTS.md) 是共享入口，`CLAUDE.md` 和 `GEMINI.md` 导入它。
- [`docs/context.md`](docs/context.md) 存放稳定事实和已确认的项目方向。
- `docs/handoffs/YYYY-MM-DD-<task>.md` 存放每次任务的决定、证据、未完成事项和接续步骤。不同任务各写自己的文件。
- 并行写代码的 agent 使用独立分支和 worktree。协调者检查证据，再集成变更。

这些文件是可版本化的交接记录，并不构成实时消息总线、跨工具调度器或完整聊天同步服务。

## 质量优先的模型配置

手动修改 [`.pstack/models.json`](.pstack/models.json)。初始 Codex 实现角色为 `gpt-6.1-sol`，判断和复杂任务为 `gpt-6-astra`，推理预算均为 `max`；独立审查使用两者。这些 ID 在本次设置会话中可用，每次换客户端仍需检查可用目录。

Pi、Claude、Gemini 尚未在本机验证，配置为 `null`，表示首次使用时发现可用模型并按角色配置，不能照搬 Codex 或 Cursor 的模型 ID。用户修改优先于默认策略。配置是供 agent 读取的角色选择约定，尚无程序化跨提供商派发器，也尚未实现“一句话生成配置”的产品功能。

## 上游实践与适配

采用官方指南中的目标驱动路由、按需读取原则、先复现再修复、真实行为验证、独立审查、每个并行写入者独立 worktree、可追溯交接记录。按 PStack 的 **Build the Lever** 和 **Prove It Works** 原则，安装与发现验证均有可重跑脚本。

完整适配边界见 [`.pstack/compatibility.md`](.pstack/compatibility.md)。上游提到的 `cursor-team-kit`、Cursor 云端调度、Bun 和 Bash 辅助程序没有被全局安装。缺失能力必须明确报告；不能声称某个未安装的工具已经运行。目前没有应用可驱动，因此先验证配置，应用确定后再用 PStack 创建真实的项目验证流程。

## 更新 PStack

只有显式升级才跟随上游 `main`。下面的命令下载上游最新版本，暂存新的版本指针，再生成并检查 skills。暂存不等于提交，检查失败时先解决问题。

```powershell
git submodule update --remote --checkout -- .pstack/upstream
git add .pstack/upstream
node scripts/pstack.mjs setup
node scripts/pstack.mjs check
git diff --cached --submodule=log
```

安装了 Codex 时，再运行 `node scripts/pstack.mjs isolate-codex` 验证新的技能列表。审查上游改动和跨客户端适配，通过后将版本指针提交到主仓库：

```powershell
git commit -m "Update PStack"
git push
```

其他机器拉取主仓库后，按记录的版本同步：

```powershell
git pull
git submodule update --init --checkout -- .pstack/upstream
node scripts/pstack.mjs setup
```

普通 `setup` 不追随上游最新版本；已初始化的 submodule 如果与暂存区记录不同，会提示先同步或暂存有意的升级。上游工作目录有改动时会拒绝生成。生成目录由脚本管理，重新生成会移除上游已删除的文件和技能；项目自己的改动应放在适配脚本或共享配置中。

## 参考资料

- [作者的 PStack README](https://github.com/cursor/plugins/blob/main/pstack/README.md) 与 [官方指南](https://github.com/cursor/plugins/tree/main/pstack/docs/guide)。作者是 Lauren Tan（@poteto），不是 Pedro；没有用无法核实的视频内容补充配置要求。
- [Codex skills](https://learn.chatgpt.com/docs/build-skills)、[项目配置参考](https://learn.chatgpt.com/docs/config-file/config-reference)、[项目过滤限制](https://github.com/openai/codex/issues/20210) 与 [实际过滤来源](https://github.com/openai/codex/blob/main/codex-rs/config/src/skills_config.rs)。
- [Claude 设置源范围](https://code.claude.com/docs/en/agent-sdk/claude-code-features#control-filesystem-settings-with-settingsources) 与 [skills 可见性](https://code.claude.com/docs/en/skills#override-skill-visibility-from-settings)。
- [Pi 资源加载参数](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/cli.md#resources)。
- [Gemini skills](https://geminicli.com/docs/cli/skills/)、[配置](https://geminicli.com/docs/reference/configuration/) 与 [列表实现](https://github.com/google-gemini/gemini-cli/blob/main/packages/cli/src/commands/skills/list.ts)。

PStack 上游使用 [MIT 许可证](https://github.com/cursor/plugins/blob/23e4138daa01c42d4969f7a5465f82704e64f798/pstack/LICENSE)。新增项目文件的开源许可证尚未选定。
