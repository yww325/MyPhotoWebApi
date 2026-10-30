# 部署指南（Deploy Guide）

本文件描述如何在机器 `SG24VMBOX` 上把 MyPhotoWebApi 部署到运行中的实例。
**AI 助手在做部署时，请先读本文件，再照着「给 AI 的执行步骤」一节操作。**

部署脚本：`scripts/deploy-instance.sh`（本仓库，bash）。
架构与配置来源的说明见 [ARCHITECTURE.md](ARCHITECTURE.md) 第 4、5 节。

---

## 1. 一分钟速览

```bash
cd /home/yww/Coding/MyPhotoWebApi-feature

# 0) 体检：只读审计「现在正在跑的东西」（不需要 --target，什么都不改）
bash scripts/deploy-instance.sh --check --instance 5000
bash scripts/deploy-instance.sh --check --instance 5001

# 1) 先做只读预演（不改任何东西）
bash scripts/deploy-instance.sh --instance 5000 --target <commit-sha> --build --dry-run

# 2) 预演通过后再真正部署
bash scripts/deploy-instance.sh --instance 5000 --target <commit-sha> --build

# 3) 出问题就回滚
bash scripts/deploy-instance.sh --instance 5000 --rollback
```

`--instance` 取 `5000` / `1`（实例 1）或 `5001` / `2`（实例 2）。
`--target` 是**目标部署目录名**，通常就是 commit 的短 sha（如 `52cc9e0`）；也可以带后缀（如 `52cc9e0-minimax`），后缀只作标签，脚本会用 DLL 里内嵌的 `1.0.0+<commit-sha>` 独立复核它。

`--check` 与 `--dry-run` 的区别：`--dry-run` 预演**一次将要发生的部署**（需要一个 `--target`）；
`--check` 审计**当前已经部署并在运行的实例**（不需要 `--target`）。`--check` 是只读的：
不复制文件、不改 drop-in、不重启服务，只读取 drop-in / `appsettings.json` / `/proc/<pid>` 并
发几个 HTTP GET，然后按同样的四个阶段逐条打印 PASS/FAIL，最后以非零码退出表示有断言失败。
对方实例的端口用 `--peer-port` 传入（默认是另一个实例），所以 5000 和 5001 走同一份逻辑。

---

## 2. 两个实例

| | 实例 1 | 实例 2 |
|---|---|---|
| systemd 用户服务 | `myphoto5000.service` | `myphoto5001.service` |
| 监听 | `127.0.0.1:5000` | `127.0.0.1:5001` |
| nginx 外部路径 | `/MyPhotos/` | `/MyPhotos2/` |
| Mongo 库 | `MyPhotos` | `MyPhotos2` |
| 照片根目录 | `/home/yww/FtpRoot/PicRootFolder` | `/home/yww/FtpRoot/PicRootFolder2` |
| 部署根目录 | `/home/yww/deploy/myphoto5000/` | `/home/yww/deploy/myphoto5001/` |
| 密码文件 | `/home/yww/.openclaw/workspace/Coding/MyPhotoWebApi/UserPass.txt` | `/home/yww/UserPass2.txt` |

**两个实例跑的是同一份构建，但配置不同。** 差异只体现在各自 deploy 目录里的
`appsettings.json`（三个键：`RootFolder` / `DatabaseName` / `UserPassLocation`）。
部署脚本保证这三个键在换版本时**逐字不变**。

---

## 3. 脚本做了什么

`scripts/deploy-instance.sh` 把「部署」拆成四个阶段的断言。任何一条不通过就**立刻中止**，
不会留下半成品（旧 deploy 目录始终保留，可回滚）。

### 阶段 1 · 部署前（只读）
- 目标标签能解析到 commit，且与仓库 HEAD 一致
- drop-in 存在，当前 `WorkingDirectory` 指向的旧 deploy 目录存在
- 目标目录不存在（除非 `--force`）
- 旧目录的 `appsettings.json` 存在、**不含 `Vlm` 段**、且不是仓库那份
- 新目标将使用的 `RootFolder` / `DatabaseName` / `UserPassLocation` 与旧目录**逐字相等**
- 旧目录的值与该实例的预期一致（5000 ↔ `MyPhotos` ↔ `PicRootFolder`）
- drop-in 里**没有** `Environment=MyPhotoSettings__RootFolder|DatabaseName|UserPassLocation|FileUrl=`
  （这类环境变量会静默覆盖 appsettings.json，是最危险的坑）
- drop-in 保留 `ExecStart=` 重置行和 7 行 `MyPhotoSettings__Vlm__*`
- 源 publish 目录存在、含 4 个主要产物；其 DLL 内嵌的 `1.0.0+<sha>` 与目标 commit 相符

### 阶段 2 · 复制后
- 把旧目录的 `appsettings.json` 暂存 → 拷贝 publish 产物 → **用暂存的那份覆盖回去**
  （publish 产物里的 appsettings 是仓库那份，带 `Vlm` 段，绝不能留在部署目录里）
- 对源目录和目标目录各生成一份**文件级 sha256 清单**（排除 `appsettings.json`），
  逐个比对：缺文件 / 内容不同 / 多出文件，三类都要为零
- 目标的 `appsettings.json` 必须与旧目录**逐字节相同**，且**不等于**仓库那份

### 阶段 3 · 重启后
- `/proc/<pid>/cwd` 指向新 deploy 目录
- `/proc/<pid>/exe` 的 sha256 == 目标目录里 `MyPhotoWebApi` 的 sha256
  （确认跑的确实是刚放进去那份）
- drop-in 的 `WorkingDirectory` 和 `ExecStart` 已指向新目录
- 目标 `appsettings.json` 的 `DatabaseName` / `RootFolder` 与实例对应关系正确
- 运行中进程的 environ 里没有那三个身份键的覆盖项（只打印键名，不打印值）

### 阶段 4 · 存活与隔离（端到端）
- `GET /health` → 200，且 body 里有 `"status":"ok"`
- 本实例根目录下的某个文件 → 本实例 **200**
- **同一个路径在对方实例 → 404**
- 对方根目录独有的文件 → 本实例 **404**，对方实例 **200**

第 4 条不检查配置字符串，而是**直接验证「两个实例行为隔离」真的生效**，
一次能抓住三类事故：配置错配、目录复制错、数据库连错。

### 安全性
- 旧 deploy 目录**从不删除**
- 目标目录已存在时**拒绝覆盖**（要 `--force`）
- 仓库的 `appsettings.json` **永远不会被拷进部署目录**
- drop-in **原子写入**（先写临时文件再 `mv`），`Environment=` 行逐字保留
- 改写前把旧 drop-in 备份成 `feature-deploy.conf.bak.<时间戳>`
- **从不读取或打印密钥内容**（API key 文件、密码文件都不碰）
- `--dry-run` 模式下除了 `--build` 生成的临时 publish 目录，**不写任何东西**

---

## 4. 给 AI 的执行步骤

部署是**影响生产的操作**。请严格按下面的顺序，并遵守第 5 节的边界。

### 步骤 1 · 确认代码已推送
```bash
cd /home/yww/Coding/MyPhotoWebApi-feature
git fetch origin
git log --oneline -1 origin/main
```
部署目录名要用**已经 push 的 commit**。未 push 的 commit 部署出去，等于把只存在于本机的代码变成生产代码。

### 步骤 2 · 同步本地仓库
```bash
git checkout main && git pull --ff-only origin main
```
> 注意：**`git pull` 对生产没有任何影响**。生产跑的是 `/home/yww/deploy/` 里的文件，
> 既不是 git 检出、也不受 git 管辖。「拉了新 commit」到「生产跑上新代码」之间，
> 还隔着「跑一次部署脚本」这一步。

### 步骤 3 · 只读预演
```bash
bash scripts/deploy-instance.sh --instance 5000 --target <commit-sha> --build --dry-run
```
逐行读输出。**必须确认 `RESULT: ALL CHECKS PASSED` 且 `failures : 0`。**
有任何 FAIL 就停下来报告，不要继续。

### 步骤 4 · 真正部署
```bash
bash scripts/deploy-instance.sh --instance 5000 --target <commit-sha> --build
```
再确认一次 `RESULT: ALL CHECKS PASSED`。脚本会打印新的 deploy 目录和保留的旧目录。

### 步骤 5 · 独立复核
脚本自己会验，但建议再手工看一眼：
```bash
export XDG_RUNTIME_DIR=/run/user/$(id -u)
systemctl --user status myphoto5000 --no-pager | head -5
curl -s http://127.0.0.1:5000/health
journalctl --user -u myphoto5000 -n 40 --no-pager
```

### 步骤 6 · 部署第二个实例
实例 2 是**完全独立**的一次部署，要单独跑一遍脚本：
```bash
bash scripts/deploy-instance.sh --instance 5001 --target <commit-sha> --build --dry-run
bash scripts/deploy-instance.sh --instance 5001 --target <commit-sha> --build
```

---

## 5. 边界（重要）

- **不要**手改 deploy 目录里的 `appsettings.json`——下次发布会被覆盖，而且脚本会校验它。
- **不要**手改 base unit（`~/.config/systemd/user/myphoto500{0,1}.service`）里的路径。
  base unit 指向 deploy 目录，正是为了让 drop-in 失效时也能指向正确的构建。
- **不要**删 drop-in（`myphoto500{0,1}.service.d/feature-deploy.conf`）。
  它是 `MyPhotoSettings__Vlm__*` 这 7 个环境变量的唯一来源；生产 `appsettings.json`
  故意不含 `Vlm` 段，删掉等于丢掉全部 VLM 配置，AI 打标会**静默失效**
  （`/api/v1/Photos/ai-tags` 恒回 503）。
- **不要**在 drop-in 里加 `Environment=MyPhotoSettings__RootFolder=...` 之类的身份键。
  环境变量的优先级高于 appsettings.json，会**静默**改掉照片根目录和数据库，
  且启动时**零报错**（`Startup.cs` 只检查路径是不是绝对路径，不检查是不是该实例的那个）。
  脚本的阶段 1 和阶段 3 都会拦这个。
- **不要**用 `git add .` / `git add -A` 提交；精确 add 文件。
- **不要**在没有 dry-run 通过的情况下直接跑真实部署。

---

## 6. 回滚

旧 deploy 目录一律保留，所以回滚只是把 drop-in 指回去再重启：
```bash
# 回滚到脚本记录的上一版
bash scripts/deploy-instance.sh --instance 5000 --rollback

# 或显式指定
bash scripts/deploy-instance.sh --instance 5000 --rollback --rollback-to cf0c3f9-minimax
```
回滚同样会跑阶段 3、阶段 4 的断言，所以**回滚后也是被验证过的**。

手动回滚（脚本不可用时）：
```bash
export XDG_RUNTIME_DIR=/run/user/$(id -u)
cd ~/.config/systemd/user/myphoto5000.service.d
cp feature-deploy.conf.bak.<时间戳> feature-deploy.conf   # 换回旧路径
systemctl --user daemon-reload && systemctl --user restart myphoto5000
```

---

## 7. 故障排查

| 现象 | 原因 | 处理 |
|---|---|---|
| `target label ... resolves to commit` FAIL | 标签不是有效 commit | 用 `git log --oneline` 查真实 sha |
| `source DLL commit matches target label commit` FAIL | publish 产物和目标标签不是一个 commit | 重新 `--build`，或改 `--target` |
| `drop-in contains an environment override` FAIL | drop-in 里混进了身份键 | 人工删掉那行（这正是要拦的事故） |
| `target appsettings.json is byte-identical ...` FAIL | 旧目录的 appsettings 已被污染 | 查旧目录的 `appsettings.json`，恢复成实例自己的那份 |
| `the two instances share a photo root; isolation is broken` | 两个实例指向了同一个根目录 | **立即回滚**，人工核对两个 deploy 目录的 appsettings |
| 服务起不来 | 看 `journalctl --user -u myphoto5000 -n 50` | 旧目录还在，回滚即可 |

脚本在失败时会打印 journal 片段；`RESULT: FAILED` 一定伴随非零退出码，
**不要在失败后继续往下走**。

---

## 8. 脚本参数速查

| 参数 | 说明 |
|---|---|
| `--instance <5000\|5001\|1\|2>` | 目标实例（必填） |
| `--target <label>` | 目标 deploy 目录名（必填），一般是 commit 短 sha |
| `--source-dir <dir>` | 已有 publish 产物目录；默认 `<repo>/MyPhotoWebApi/publish` |
| `--build` | 先跑 `dotnet publish` 到临时目录再部署 |
| `--dry-run` | 只做只读检查，不写不重启 |
| `--force` | 允许覆盖已存在的目标目录 |
| `--skip-restart` | 不重启（阶段 3 的进程相关检查会跳过） |
| `--rollback` | 回滚 |
| `--rollback-to <name>` | 回滚到指定目录名 |
| `--keep-build-dir` | `--build` 时保留临时 publish 目录（排查用） |
| `-h, --help` | 帮助 |

环境变量（仅测试用，脚本会拒绝在 sandbox 下调用 systemctl）：
`DEPLOY_BASE`（默认 `/home/yww/deploy`）、`SYSTEMD_USER_DIR`（默认 `~/.config/systemd/user`）。

---

*文档更新日期：2026-10-28*
