# MyPhotoWebApi
this is server side code using Mongo DB and oData
play with it with the swagger page first
front end code at 
 https://github.com/yww325/ng-app
 
## 文档

- [ARCHITECTURE.md](ARCHITECTURE.md) — 架构、接口、配置来源、nginx 与 systemd 说明
- [DEPLOY.md](DEPLOY.md) — **部署指南**：用 `scripts/deploy-instance.sh` 部署到运行中的实例

## 部署

机器 `SG24VMBOX` 上跑着两个实例（5000 / 5001），共用一份构建但各用一套配置。
部署一律走脚本，它会在部署前中后做四阶段断言，配置被改错时立刻中止：

```bash
# 只读预演
bash scripts/deploy-instance.sh --instance 5000 --target <commit-sha> --build --dry-run
# 真正部署
bash scripts/deploy-instance.sh --instance 5000 --target <commit-sha> --build
# 回滚
bash scripts/deploy-instance.sh --instance 5000 --rollback
```

详细说明（含给 AI 的执行步骤和边界）见 [DEPLOY.md](DEPLOY.md)。
