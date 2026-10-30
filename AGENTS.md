# AGENTS.md — MyPhotoWebApi

ASP.NET Core 8 照片管理 Web API。解决方案 `MyPhotoWebApi.sln` 在仓库根，唯一项目
`MyPhotoWebApi/MyPhotoWebApi.csproj`（`net8.0`）。生产是 `SG24VMBOX` 上的两个实例
（5000 / 5001），部署细节见 [DEPLOY.md](DEPLOY.md)。

## 发布门禁（最重要）

**没有通过 Release 构建、并且没有在 5000 上真跑通（`/health` 200），不许 push 到 main。**

顺序固定，不要跳步：

1. 改代码，`git commit`（只在本地，先不 push）
2. `dotnet publish -c Release` —— 必须 0 error
3. `bash scripts/deploy-instance.sh --instance 5000 --target <sha> --build`
   四阶段全 PASS，再 `curl -s http://127.0.0.1:5000/health` 拿到 200
4. 以上都过了，才 `git push origin main`
5. 再部署 5001，同样四阶段 + `/health` 200

push 是终点，不是中点。main 必须始终指向一个**已验证可发布、已验证可运行**的 commit。

> 与 DEPLOY.md 的差异：DEPLOY.md「给 AI 的执行步骤」把「确认代码已推送」放在第一步，
> 本文把 push 挪到 5000 验证之后。两者冲突时以本文为准。

**为什么**：2026-10-30，ImageSharp 3.1.12 → 3.2.0 只跑了 `dotnet build`（默认 Debug）
就判定"验证通过"并 push，随后 `dotnet publish -c Release` 失败——3.2.0 新增 license
校验，且 `ContinueOnError` 只对 Debug 生效。结果 main 有一段时间不可发布。

## 验证命令必须与部署一致

- 部署脚本执行的是 `dotnet publish ... -c Release`。任何"构建通过"的结论都必须由
  **`-c Release`** 得出；Debug 通过 ≠ 可发布。
- 包升级尤其危险：新包会往构建里注入自己的 MSBuild targets/props，改变的是构建
  行为本身。升包时先 publish，再谈其它。

## 已知陷阱

- **ImageSharp ≥ 3.2.0 需要 Six Labors 许可证**（`SixLaborsLicenseKey` /
  `SixLaborsLicenseFile` / `sixlabors.lic`）。没有 key 时 Release publish 报
  `No Six Labors license found`；3.1.12 及更早没有这个校验。
- `NU1902` / `NU1903` 是 NuGet 审计告警（moderate / high 漏洞）。出现即说明依赖有
  已知 CVE，升级后应归零。
- `.cs` 源文件多为 **UTF-8 BOM + CRLF**，编辑工具改成 LF 会让 diff 膨胀成整文件
  重写。改完先看 `git diff --stat`。

## 部署与运行

- 实例：`myphoto5000.service`（库 `MyPhotos`）与 `myphoto5001.service`（库
  `MyPhotos2`）；部署目录 `/home/yww/deploy/myphoto<N>/<sha>/`，旧目录永不删除，可回滚。
- 非交互 shell 里 `systemctl --user` 需要 `export XDG_RUNTIME_DIR=/run/user/$(id -u)`。
- 配置：每个实例的 `appsettings.json` 由部署目录自带（含 `Vlm` 段），systemd unit 不再
  注入 `Environment=`。密钥只以**路径**出现（`ApiKeyLocation`），值不打印、不入库。
