# AGENTS.md — 项目指令（WorkBuddy 版）

## 项目是什么

本目录是一套**可复制的「中文自然语言 → Git 命令」对照表**，让 WorkBuddy 在任意项目里听懂中文 git 需求。

| 文件 | 说明 |
|---|---|
| `AGENTS.md` | 本文件。WorkBuddy 每次会话自动加载，是 AI 的行为入口 |
| `git-AI指令表.yaml` | 机器可读的「提示词 → 命令 + 风险等级」完整映射，含 WorkBuddy 执行环境约定 |
| `git-操作对照表.md` | 给人看的可读版，含平台差异说明与危险等级速查 |

## Git 操作约定（重要）

本项目用 git 管理。用户会用**中文自然语言**提 git 需求（例如「提交一下」「回退上一条」）。

1. **先读 `git-AI指令表.yaml`**：按其中的 `intent` / `prompts` 匹配用户的话，执行对应的 `command`；同时遵守该文件的 `platform` 节（WorkBuddy 执行环境）。
2. **按风险等级处理**（见该表 `risk_levels`）：
   - `safe`：直接执行并报告；
   - `caution`：执行前一句话说明影响，执行后报告；
   - `destructive`：**先列出受影响的文件 / 提交范围并用询问工具确认**，再执行，完成后回读校验；
   - `history-rewrite`：先说明后果并确认；若该历史已推送或在共享分支上，必须特别提醒，优先改用 `git revert` 或新提交。
3. **WorkBuddy 执行环境**：默认 shell 是 Git Bash（POSIX sh），路径用正斜杠与绝对路径；工作目录跨命令持久但环境变量不持久（须内联到同一条命令）；工具没有 workdir 参数，子仓库用 `git -C <绝对路径>`；命令非交互，禁用 `rebase -i`、`add -p`、不带 `-m` 的 commit；统一用 `git --no-pager`；命令被权限策略拒绝即停止，不换工具重试；Ask 模式只读不执行、Plan 模式先出计划。
4. **平台内置 Git 约定**：优先新建提交而非 `--amend`；破坏性操作先考虑更安全的替代（`revert` / `restore`）；不加 `--no-verify` 跳过钩子；不连发命令（有依赖用 `&&`）；长耗时命令走后台任务；`.workbuddy/` 是项目数据目录，不清理不删除。
5. **敏感文件红线**：含明文密码/密钥的配置文件（如 `ScreenBlanker.ini`）、`dist/`、`build/`、`*.exe` **永不提交**
   （`.gitignore` 已覆盖，**不要**用 `git add -f` 强行添加）；提交前先扫产物、大文件（>10MB）与敏感配置。
6. **汇报用中文**，格式：改了什么 → 执行结果（附 `git status --short` / `git --no-pager log --oneline -n 5` 摘要）。
7. 提交信息由你依据改动内容拟一句中文概述，除非用户指定。

## 复用方式

- **复制三件套（最简单）**：新建项目时，把 `AGENTS.md`、`git-AI指令表.yaml`、`git-操作对照表.md` 一起复制到新项目根目录；WorkBuddy 会自动加载 `AGENTS.md`，按同样规则操作 git。
- **封装成 Skill（免复制）**：把 `git-AI指令表.yaml` 作为参考文件放进 `~/.workbuddy/skills/<技能名>/`（用户级，所有项目可用）或 `<项目>/.workbuddy/skills/<技能名>/`（项目级），在 `SKILL.md` 的 description 里写明「用户用中文自然语言提出 git 操作需求时按此表执行」，即可跨项目复用。
