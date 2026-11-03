# 发布 App 新版本（VersionUpdate）

本文记录怎么把一个新版 MyPhotoAdmin 发到服务器上，让手机上已装的旧版能通过「检测更新」按钮升级。服务端的接口说明见 [ARCHITECTURE.md](ARCHITECTURE.md) 的「应用更新」一节。

## 流程总览

```
改版本号 → 构建 APK → 用固定 keystore 签名 → 放进服务器的 ApkFolder → 写 version.json → 验证
```

不需要重启任何服务：`version.json` 和 APK 都是每次请求时现读的。两个实例（5000 / 5001）共用同一个 `ApkFolder`，所以发布一次两边都生效。

## 1. 改版本号

在 MyPhotoAdmin 仓库的 `app/build.gradle.kts` 里：

```kotlin
versionCode = 3          // 每次发布必须 +1，只能变大
versionName = "1.0.2"    // 给人看的版本号
```

**`versionCode` 是唯一的判断依据**：服务器上 `version.json` 的 `versionCode` 比手机上装的 App 大，App 才会提示更新；一样或更小只会说「已经是最新版本」。忘了加就会出现「发了新版但没人能更新」的情况。

## 2. 构建 APK

需要 JDK 17+ 和 Android SDK 37：

```bash
cd MyPhotoAdmin
./gradlew clean assembleRelease
# 产物：app/build/outputs/apk/release/app-release-unsigned.apk
```

这个 APK 是**未签名**的，不能直接安装，下一步签名。

## 3. 签名

### 3.1 keystore 在哪

签名用的 keystore 是 MyPhotoAdmin 这套 App 的**唯一身份**，换一个 key 就没法覆盖安装（详见 3.4）。

| 位置 | 用途 |
|---|---|
| GitHub 仓库 `yww325/MyPhotoAdmin` 的 Actions secret `MYPHOTOADMIN_KEYSTORE_BASE64` | 长期备份 / 以后接 CI 自动构建时用。内容是 keystore 文件的 base64，secret 只能写不能读回 |
| `/home/yww/.openclaw/workspace/Coding/MyPhotoWebApi/MyPhotoAdmin.keystore` | 构建机上的工作副本，已在 `.gitignore` 里，不会被提交 |

这台 keystore 就是标准的 Android debug key，别名和密码都是公开约定：

```
keystore 密码: android
key 别名:      androiddebugkey
key 密码:      android
```

它不是「安全密钥」，价值在于**一直用同一把**：从 secret 取回、或者从构建机复制时都别重新生成。

从 secret 恢复成文件（需要已登录 `gh`）：

```bash
gh secret list --repo yww325/MyPhotoAdmin          # 确认 secret 存在
# secret 不能直接读回，恢复方式见 3.2 末尾的说明
```

### 3.2 签名命令

```bash
cd MyPhotoAdmin
BT=$ANDROID_HOME/build-tools/37.0.0
$BT/apksigner sign \
  --ks /home/yww/.openclaw/workspace/Coding/MyPhotoWebApi/MyPhotoAdmin.keystore \
  --ks-pass pass:android --key-pass pass:android --ks-key-alias androiddebugkey \
  --out MyPhotoAdmin-1.0.2.apk \
  app/build/outputs/apk/release/app-release-unsigned.apk
```

校验签名和版本号（发布前一定做）：

```bash
$BT/apksigner verify --print-certs MyPhotoAdmin-1.0.2.apk
$BT/aapt2 dump badging MyPhotoAdmin-1.0.2.apk | head -1
# 期望看到：package: name='org.yww.myphotoadmin' versionCode='3' versionName='1.0.2'
sha256sum MyPhotoAdmin-1.0.2.apk
```

`apksigner verify` 打印的证书 SHA-256 应该是
`e5c3005a004f0cb3dc7177700af137d4e6749b93ef4364f98259510f7d5a6897`（当前这把 key）。
不是这个值就说明用错了 keystore，装上去会覆盖失败。

> **keystore 丢了怎么办**：GitHub secret 只能读给 Actions 用、不能读回内容。真丢了就只能生成新 key，
> 代价是所有用户必须卸载重装（Android 不允许换签名的覆盖安装）。所以除了 secret，构建机上也留一份工作副本。

### 3.3 换 key 的后果

Android 只允许**同一签名**的 APK 覆盖安装。签名不一致时安装会失败（`INSTALL_FAILED_UPDATE_INCOMPATIBLE`），
用户必须先卸载旧版，数据（App 里的设置、登录令牌）也会丢。所以：

- 同一台手机上装的所有版本必须用同一把 keystore；
- 不要因为「debug key 不好看」就换成新生成的 release key，除非愿意让所有人卸载重装。

### 3.4 安装权限

手机上第一次安装来自浏览器/文件管理器的 APK 时，系统会要求给那个应用「允许安装未知应用」。
App 内更新走的是系统安装界面，同一个开关。这个开关是系统行为，App 这边不需要额外处理（`REQUEST_INSTALL_PACKAGES` 已经声明）。

## 4. 发布到服务器

服务器上的 APK 目录由 `MyPhotoSettings:ApkFolder` 配置，生产是 `/home/yww/FtpRoot/ApkFolder`
（**不要**放进照片根目录，那个目录会被 `/File/` 公开出去）。

```bash
# 1) 放进目录，文件名建议 MyPhotoAdmin-<version>.apk
scp MyPhotoAdmin-1.0.2.apk yww@服务器:/home/yww/FtpRoot/ApkFolder/

# 2) 写 version.json（描述「当前发布的是哪一版」）
cat > /home/yww/FtpRoot/ApkFolder/version.json <<'EOF'
{
  "version": "1.0.2",
  "versionCode": 3,
  "fileName": "MyPhotoAdmin-1.0.2.apk",
  "notes": "这次更新改了什么（会显示在 App 的更新对话框里）",
  "releasedAt": "2026-11-03T16:00:00+08:00"
}
EOF
```

`version.json` 字段：

| 字段 | 必填 | 说明 |
|---|---|---|
| `version` | 是 | 版本名，显示在对话框标题「发现新版本 x.y.z」 |
| `versionCode` | 是 | 必须是整数，且**大于**手机上 App 的 versionCode |
| `fileName` | 否 | APK 文件名；省略时默认 `MyPhotoAdmin-<version>.apk` |
| `notes` | 否 | 更新说明，对话框里原样显示 |
| `releasedAt` | 否 | 发布时间，ISO 8601 |

注意：

- 目录里可以同时放多个版本的 APK，旧版本仍然能通过
  `/api/v1/Update/apk/<文件名>` 直接下载（回滚就靠这个）；只有 `version.json` 决定「当前发布版本」。
- 只接受目录里的 `.apk` 文件名，名字里带 `/`、`\` 的一律 404。
- 目录权限要让运行服务的用户能读（`chmod 755` 目录、`644` 文件）。
- 写完不用重启服务。

## 5. 验证

```bash
# 服务器本机
curl -s http://127.0.0.1:5000/api/v1/Update/latest
curl -s -o /dev/null -w '%{http_code} %{size_download}\n' \
  http://127.0.0.1:5000/api/v1/Update/apk/MyPhotoAdmin-1.0.2.apk
curl -s http://127.0.0.1:5001/api/v1/Update/latest        # 实例 2 应该是同一份

# 外网（经过 nginx）
curl -s https://yww325.eu.org/MyPhotos/api/v1/Update/latest
curl -s -r 0-1023 -o /dev/null -w '%{http_code}\n' \
  https://yww325.eu.org/MyPhotos/api/v1/Update/apk/MyPhotoAdmin-1.0.2.apk   # 期望 206
```

期望：

- `latest` 返回的 `versionCode` 等于 `version.json` 里的值，`size` 等于 APK 的实际字节数；
- APK 请求返回 200（整包）或 206（Range）；
- 完整下载后 `sha256sum` 和本地签名产物一致；
- 手机上装上一版 → 点「检测更新」→ 弹出新版本对话框。

## 6. 回滚

把 `version.json` 改回旧版本（旧 APK 还在目录里）：

```bash
# 例如退回 1.0.1
sed -i 's/"versionCode": 3/"versionCode": 2/; s/"version": "1.0.2"/"version": "1.0.1"/; s/MyPhotoAdmin-1.0.2.apk/MyPhotoAdmin-1.0.1.apk/' \
  /home/yww/FtpRoot/ApkFolder/version.json
```

用户再点「检测更新」就会被告知没有新版本。已经装了新版的手机不会自动降级（Android 也不允许降 versionCode 覆盖安装），
要让那些用户回到旧版只能卸载重装。

## 7. 常见问题

| 现象 | 原因 |
|---|---|
| `latest` 返回 404 `The server has not published an update yet.` | `ApkFolder` 没配、`version.json` 不存在 / JSON 坏了 / 它指的 APK 不在目录里（这几种情况服务端都会写日志） |
| App 说「已经是最新版本」 | `versionCode` 没有比 App 的大 |
| 安装时报签名冲突 | APK 用的 keystore 和手机上已装的不是同一把（见 3.3） |
| 下载 404 | 文件名和 `version.json` 里的 `fileName` 对不上，或者带了路径分隔符 |
| 下载中断 | 接口支持 Range，可以续传；nginx 那段的 `proxy_read_timeout` 是 1200s |
