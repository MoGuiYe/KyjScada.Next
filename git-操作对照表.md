# Git 操作提示词对照表（WorkBuddy 通用版）

> 用法：在 WorkBuddy 会话里直接说「提示词」列的话，我会翻译成右侧命令并在工作区执行。
> WorkBuddy 会自动加载项目根目录的 `AGENTS.md`，由它指向机器可读的 `git-AI指令表.yaml`。
> ⚠️ = 会丢弃改动或重写历史：先列出受影响范围 → 向你确认 → 执行 → 回读校验。
> 提示词只是示例，自然语言换个说法也认。
> 本表与具体项目无关：把本文件、`git-AI指令表.yaml`、`AGENTS.md` 三个一起复制到任意项目根目录即可复用；也可封装成 WorkBuddy Skill 免复制（见第 10 节）。

## 先看：WorkBuddy 平台与本地 shell 的 8 个差异

| # | 差异 | 影响 | 对策 |
|---|---|---|---|
| 1 | 默认 shell 是 **Git Bash（POSIX sh）**，另有独立的 PowerShell 5.1 工具 | 与 PowerShell 语法不同：路径用正斜杠 `/c/...`、变量用 `$VAR`、`&&` 可用；PowerShell 里 `&&` **不可用** | 一律按 bash 语法写；只有注册表 / COM / .NET 这类 Windows 专属操作才用 PowerShell 工具 |
| 2 | **工作目录跨命令持久**，但 shell 变量 / 别名 / 函数**不持久** | `cd` 会留到下一句，`export` 到下一句就丢了 | 环境变量必须内联到同一条命令：`GIT_TERMINAL_PROMPT=0 git push`；优先用绝对路径，不靠 `cd` |
| 3 | 命令**非交互执行**，禁止交互标志和会打开编辑器的命令 | `git rebase -i`、`git add -p`、`git commit` 不带 `-m` 会失败或挂住 | `commit` 必带 `-m`；压缩提交用 `reset --soft` + 新提交；不跳过 hooks |
| 4 | 工具**没有 workdir 参数**，默认工作目录就是项目根目录 | 没有可指定目录的 workdir 参数 | 子仓库用 `git -C <绝对路径> ...`；路径含空格加双引号 |
| 5 | **权限 / 安全策略会拦截命令**，且拒绝即终局 | 换个工具或等价命令重试属违规 | 被拒就停下并说明原因；破坏性操作先用询问工具确认 |
| 6 | 输出走非 tty 通道，分页器默认不启用 | 若 `core.pager` 被自定义，长输出仍可能被截断 | 统一 `git --no-pager ...`，或 `git config core.pager cat` |
| 7 | 中文路径 / 文件名可能被 `core.quotepath` 转义成八进制 | 中文文件名在 `status` / `diff` 里变乱码 | `git config core.quotepath false`；PowerShell 工具写文件加 `-Encoding utf8` |
| 8 | 有 **Agent / Plan / Ask** 三种工作模式，以及询问工具 | Ask 模式只读、不执行命令；Plan 模式先出计划再动手 | 破坏性操作先列范围并确认，不默认「直接执行」 |

> 命令写法：bash 里 `<` `>` 同样是重定向符，写真实命令时**不要**照抄占位符；Windows 路径写成 `/d/某目录`；`--` 之后的参数按原样传；有依赖的多条命令用 `&&` 串起来；不要用 `nohup` / 尾随 `&` 脱离进程；`git clone`、`git gc` 这类耗时命令走后台任务。

### WorkBuddy 内置的 Git 约定（优先级高于本表）

| 约定 | 具体做法 |
|---|---|
| 优先新建提交 | 能用一条新提交解决的，不轻易 `--amend`；用户明确说「改上一条」才用 |
| 破坏性操作先找更安全的替代 | 已推送 → `git revert` 而不是 `reset --hard`；还原文件用 `git restore` 而不是 `checkout --` |
| 不跳过钩子与签名 | 不加 `--no-verify`；pre-commit 失败要修根因，不绕过 |
| 不连发命令 | 有依赖用 `&&`；相互独立可并行调用；每步跑完核对输出 |
| 长命令走后台 | `git clone` / `git gc` 用后台任务，不干等 |
| `.workbuddy/` 是项目数据目录 | 不是缓存，不清理、不删除；平台安装目录（`resources/app.asar` 等）只读，不进用户仓库 |

## 0. 初始化与身份配置

| 场景 | 你这样说（提示词） | 我实际执行的命令 |
|---|---|---|
| 检查是不是仓库 | 这个文件夹是 git 仓库吗？ | `git rev-parse --is-inside-work-tree` |
| 初始化仓库 | 把这个文件夹变成 git 仓库 | `git init -b main` |
| 看当前身份 | 现在提交用的是什么身份？ | `git config user.name` / `git config user.email` |
| 改本仓库身份 | 把提交身份改成 张三 zhangsan@qq.com | `git config user.name "张三"` + `git config user.email "..."` |
| 改全局身份 | 以后所有仓库都用这个身份 | `git config --global user.name/email`（需你明确说「全局」） |
| 看全部配置 | 看看这个仓库的 git 配置 | `git config --local --list` |
| 关掉分页器 | 别让 git log 卡在分页器里 | `git config core.pager cat` |
| 关掉编辑器 | 提交别弹编辑器 | `git config core.editor true` |
| 中文不乱码 | 中文文件名显示成八进制了 | `git config core.quotepath false` |

## 1. 日常提交

| 场景 | 你这样说（提示词） | 我实际执行的命令 |
|---|---|---|
| 看有哪些改动 | 现在有哪些改动？ | `git status --short` |
| 带分支的简版状态 | 当前在哪个分支、改动几条？ | `git status --porcelain=v1 -b` |
| 提交全部 | 提交一下 | 先 `git status --short` 扫敏感/打包文件 → `git add -A && git commit -m "概述"` |
| 提交单个文件 | 只提交 src/main.py，信息写：修复校验 | `git add src/main.py && git commit -m "..."` |
| 看暂存了什么 | 暂存区里现在有哪些文件？ | `git --no-pager diff --cached --stat` |
| 从暂存区拿掉 | 把 config/app.ini 从暂存区拿出来，改动保留 | `git restore --staged config/app.ini` |
| 改上条提交信息 ⚠️ | 上条提交信息打错了，改成：XXX | `git commit --amend -m "XXX"`（仅你明确要求时；已推送则改用新提交） |
| 补漏提交 ⚠️ | 忘了把 docs/notes.md 加进去，补到上一条提交里 | `git add docs/notes.md && git commit --amend --no-edit`（同上） |
| 只提交已跟踪文件 | 提交已跟踪的改动，别带上新文件 | `git add -u && git commit -m "..."` |
| 预演提交内容 | 这次提交会带上哪些文件？ | `git add -n .` / `git status --short` |

## 2. 查看历史与对比

| 场景 | 你这样说（提示词） | 我实际执行的命令 |
|---|---|---|
| 历史列表 | 看提交历史 | `git --no-pager log --oneline -n 20` |
| 图形化历史 | 用图看看分支历史 | `git --no-pager log --oneline --graph --all -n 30` |
| 上次改了什么 | 上次那条提交具体动了哪些行？ | `git --no-pager show HEAD` |
| 未提交改动 | 还没提交的改动逐行列出来 | `git --no-pager diff` |
| 单文件对比 | src/main.py 相比上条提交改了什么？ | `git --no-pager diff -- src/main.py` |
| 文件完整历史 | src/main.py 被改过几次？每次都改了什么？ | `git --no-pager log --follow -p -- src/main.py` |
| 某行来历 | 第 120 行是谁、什么时候改的？ | `git --no-pager blame -L 120,120 src/main.py` |
| 按关键词搜提交 | 哪条提交里提到过「登录」？ | `git --no-pager log --grep="登录" --oneline` |
| 搜某段代码何时加入 | 什么时候加进来的 DelaySeconds？ | `git --no-pager log -S "DelaySeconds" --oneline` |
| 统计谁改得多 | 这个文件主要谁在改？ | `git --no-pager shortlog -sn -- src/main.py` |

## 3. 回退与撤销

| 场景 | 你这样说（提示词） | 我实际执行的命令 |
|---|---|---|
| 丢弃单文件改动 ⚠️ | 把 src/main.py 恢复到上次提交的样子 | `git restore src/main.py` |
| 丢弃全部未提交改动 ⚠️ | 全部未提交的改动都不要了 | `git restore .`（新文件不受影响，见下一行） |
| 连新增文件一起清掉 ⚠️ | 未跟踪的新文件也一起删掉 | 先 `git clean -n` 预演并列清单 → 确认 → `git clean -fd` |
| 撤提交、改动留着 | 撤掉最近一条提交，代码改动留着 | `git reset --soft HEAD~1` |
| 撤提交、改动留在工作区 | 撤掉最近一条提交，但改动不要暂存 | `git reset HEAD~1` |
| 彻底回退 ⚠️ | 彻底回到 3 条提交之前，改动都不要 | `git reset --hard HEAD~3`（已推送则改 `git revert`） |
| 安全撤销（已推送过） | v1.0 那条提交有问题，用反向提交撤掉它 | `git revert <提交号>` |
| 找回误删提交 ⚠️ | 我不小心 reset 丢了提交，帮我找回来 | `git --no-pager reflog` + `git reset --hard HEAD@{n}` |
| 找回误删文件 | 我不小心删了 src/main.py，从上次提交找回来 | `git restore src/main.py` |
| 从某条提交取出文件 | 把上次提交里的 config 拿回来 | `git restore --source=HEAD~1 -- config/` |

## 4. 分支

| 场景 | 你这样说（提示词） | 我实际执行的命令 |
|---|---|---|
| 新建并切换 | 开个实验分支 test-ui | `git switch -c test-ui` |
| 切换分支 | 切回 main | `git switch main` |
| 列出分支 | 现在有哪些分支？ | `git branch -a` |
| 合并分支 | 把 test-ui 合回 main | `git switch main && git merge test-ui` |
| 解决冲突 | 有冲突了，帮我分析怎么选 | 读 `git status` / 冲突文件 diff，逐个冲突列选项让你选 |
| 删已合并分支 | test-ui 合并完了，删掉它 | `git branch -d test-ui` |
| 强删未合并分支 ⚠️ | 那个分支不要了，直接删 | `git branch -D test-ui` |
| 重命名分支 | 把分支 xxx 改名叫 yyy | `git branch -m xxx yyy` |
| 摘单条提交 | 把 test-ui 上那条「修复校验」摘到 main | `git cherry-pick <提交号>` |
| 变基 ⚠️ | 把 test-ui 变基到 main 最新上 | `git rebase main` |
| 压缩零碎提交 ⚠️ | 把最近 3 条零碎提交压成一条 | 无交互做法：`git reset --soft HEAD~3 && git commit -m "..."`（WorkBuddy 禁止 `rebase -i`） |
| 中断变基 | 变基卡住了，退回去 | `git rebase --abort` |
| 并行目录开发 | 另开一个目录看别的分支 | `git worktree add ../wt-test test-ui` |

## 5. 暂存（stash）「临时抽屉」

| 场景 | 你这样说（提示词） | 我实际执行的命令 |
|---|---|---|
| 塞抽屉 | 手头改动先塞抽屉，我要干别的 | `git stash push -m "说明"` |
| 连未跟踪文件一起塞 | 新文件也一起塞进去 | `git stash push -u -m "说明"` |
| 看抽屉 | 抽屉里有什么？ | `git stash list` |
| 取出并清空该抽屉 | 取出暂存的改动 | `git stash pop` |
| 取出但保留抽屉 | 取出暂存，抽屉留着 | `git stash apply` |
| 扔掉某个抽屉 ⚠️ | 把第 2 个暂存扔掉 | `git stash drop stash@{1}` |
| 清空抽屉 ⚠️ | 抽屉全部清空 | `git stash clear` |

## 6. 标签

| 场景 | 你这样说（提示词） | 我实际执行的命令 |
|---|---|---|
| 打带说明的标签 | 给现在版本打标签 v1.0 | `git tag -a v1.0 -m "说明"` |
| 列出标签 | 现在有哪些标签？ | `git tag -n` |
| 看某标签内容 | v1.0 标签对应哪次提交？ | `git --no-pager show v1.0` |
| 删标签 | 把标签 v1.0 删掉 | `git tag -d v1.0` |
| 推送标签 | 把标签也推上远程 | `git push origin v1.0` / `git push --tags` |

## 7. 忽略规则（.gitignore）

| 场景 | 你这样说（提示词） | 我实际执行的命令 |
|---|---|---|
| 加忽略规则 | 把 *.log 加进忽略列表 | 编辑 `.gitignore` 追加规则并提交 |
| 已跟踪文件退出跟踪 | config/app.ini 被跟踪了，移出版本库但本地保留 | `git rm --cached config/app.ini` + 加 `.gitignore` |
| 查为什么没提交 | 为什么 xxx 没被提交？ | `git check-ignore -v xxx` |
| 看哪些文件会被提交 | 这次提交会带上哪些文件？ | `git status --short` 或 `git add -n .` |

## 8. 远程仓库（GitHub / Gitee / 内网 GitLab）

| 场景 | 你这样说（提示词） | 我实际执行的命令 |
|---|---|---|
| 连远程 | 我的仓库地址是 https://… 连上它 | `git remote add origin <URL>` |
| 看远程 | 现在连的哪个远程仓库？ | `git remote -v` |
| 推送 | 推上远程 | `GIT_TERMINAL_PROMPT=0 git push`（先确认分支名与上游） |
| 首次推送并设上游 | 第一次推这个分支 | `GIT_TERMINAL_PROMPT=0 git push -u origin HEAD` |
| 推标签 | 把标签也推上去 | `git push --tags` |
| 拉取 | 拉取云端最新 | `GIT_TERMINAL_PROMPT=0 git pull --ff-only`（避免意外合并提交） |
| 只看不拉 | 云端有什么新动静？先别拉 | `GIT_TERMINAL_PROMPT=0 git fetch` + `git --no-pager log HEAD..@{u} --oneline` |
| 改远程地址 | 远程地址改到新的 URL | `git remote set-url origin <URL>` |
| 克隆 | 把 https://… 克隆到 D:\某目录 | `git clone <URL> /d/某目录`（耗时，走后台任务） |
| 凭据不弹窗 | 别让它问我要密码 | 环境变量**内联**到同一条命令：`GIT_TERMINAL_PROMPT=0 git ...`；改用凭据管理器或 SSH |

> 注：PowerShell 工具里等价写法是 `$env:GIT_TERMINAL_PROMPT='0'; git push`。环境变量不跨命令保留，不能先 `export` 再单独跑 git。

## 9. 其他实用功能

| 场景 | 你这样说（提示词） | 我实际执行的命令 |
|---|---|---|
| 导出干净压缩包 | 把当前版本导出成 zip（不带 .git） | `git archive --format=zip -o 名称.zip HEAD` |
| 提交前自动检查 | 提交前自动检查语法 | 写 `.git/hooks/pre-commit`（按项目语言选 lint 命令，失败要修根因） |
| 引入另一个项目 | 把 XX 项目作为子模块加进来 | `git submodule add <URL> <路径>` |
| 压缩整理仓库 | 把仓库压缩整理一下 | `git gc`（走后台任务） |
| 体检仓库 | 仓库有没有坏对象？ | `git fsck --no-progress` |
| 看仓库多大 | 仓库为什么这么大？ | `git count-objects -vH` |
| 揪大文件 | 找出历史里最大的文件 | `git rev-list --objects --all` + `git cat-file --batch-check` 按大小排序 |

## 10. WorkBuddy 场景专项

| 场景 | 你这样说（提示词） | 我的做法 |
|---|---|---|
| 在子目录 / 子仓库里操作 | 对某个子项目做 git 操作 | `git -C <绝对路径> status` 这类写法，不靠 `cd`（工具没有 workdir 参数），路径写成 `/c/...` `/d/...` |
| 提交前排雷 | 提交前检查有没有不该进库的文件 | 扫 `node_modules/`、`dist/`、`build/`、`*.exe`、`*.asar`、`*.rar`、`*.zip`，以及含明文密码/密钥的配置文件，命中就拦下 |
| 大文件预警 | 这次提交会不会带大文件？ | 提交前 `git status --short` + 检查体积，超 10MB 先提醒 |
| 别动平台自身文件 | 别把安装目录 / 配置目录提交进去 | 平台安装目录（`resources/app.asar` 等）只读、不进用户仓库；`.workbuddy/` 是项目数据目录，不清理不删除，按需加进 `.gitignore` |
| 命令被拦下 | 提示没有权限 | 立即停止并向你说明，**不**换工具或等价命令重试 |
| 想先看看再动手 | 先告诉我你会改哪些文件 | 切 Ask 模式只读分析，或 Plan 模式先出计划；不执行任何写操作 |
| 确认危险操作 | 要清掉全部未提交改动 | 用询问工具列出受影响文件，你确认后再执行，完成后回读校验 |
| 跨项目免复制 | 每个项目都要带这三个文件吗 | 把 `git-AI指令表.yaml` 作为参考文件封装成用户级 Skill（`~/.workbuddy/skills/`）或项目级 Skill（`<项目>/.workbuddy/skills/`），一次安装全项目可用 |

## 危险等级速查

| 等级 | 操作 | 我的做法 |
|---|---|---|
| 🟢 安全（只读 / 纯新增） | status / log / diff / show / blame / tag / stash list / fetch / check-ignore / init / add | 直接执行，中文汇报 |
| 🟡 改变状态但可恢复 | commit / restore --staged / switch / merge / stash pop / revert / push / pull | 执行前一句话说明影响，执行后附 `git status --short` / `git --no-pager log --oneline -n 5` 摘要 |
| 🟠 会丢改动 | restore / reset / stash drop / branch -D / clean / rebase | 先列出受影响文件或提交范围 → 询问确认 → 执行 → 回读校验 |
| 🔴 改写历史 | reset --hard 已推送 / push --force / 删远程分支 / rebase 已共享分支 / amend | 先说明后果并确认；已推送或共享分支必须特别提醒，优先改用 `git revert` 或新提交 |
