# MyPhotoWebApi 项目配置架构说明

本项目基于 **.NET 8.0** 和 **OData 8.x**（`Microsoft.AspNetCore.OData` 8.2.5），数据存放在 MongoDB，以“虚拟目录”模式部署在 Nginx 后面。

## 1. 核心架构设计
项目采用**全透明挂载**架构。C# 代码不感知外部挂载路径（如 `/MyPhotos`），路径映射由 Nginx 转发规则完成：Nginx 切掉前缀后转发给程序，并通过 `X-Forwarded-Prefix` 请求头告诉程序外部前缀（Swagger 页面用它生成正确的地址）。因此同一份程序可以挂载到不同的虚拟路径，目前同一台机器上运行着两个实例：

| 实例 | 外部路径 | 本地端口 | 数据库 | 照片根目录 |
|---|---|---|---|---|
| 1 | `/MyPhotos/` | `127.0.0.1:5000` | `MyPhotos` | `/home/yww/FtpRoot/PicRootFolder` |
| 2 | `/MyPhotos2/` | `127.0.0.1:5001` | `MyPhotos2` | `/home/yww/FtpRoot/PicRootFolder2` |

本仓库是实例 1 的源码。实例 2 运行的是 `/home/yww/Coding/MyPhotoWebApi2` 下单独的一份代码。

## 2. 后端配置 (.NET 8.0)
- **监听地址**：`http://127.0.0.1:5000`（只监听本机 IPv4，由启动参数 `--urls` 指定）。
- **运行环境**：`ASPNETCORE_ENVIRONMENT=Production`，读取发布目录下的 `appsettings.json`。
- **主要配置项**（`MyPhotoSettings`）：
    - `RootFolder`：照片根目录。
    - `FileUrl`：原图的访问前缀，默认 `/File`。
    - `ConnectionString` / `DatabaseName`：MongoDB 连接（本机 `127.0.0.1:27017`）。
    - `UserPassLocation`：密码文件的**绝对路径**。程序**只在启动时**读取一次，并保存它的 MD5；修改密码后需要重启服务。
- **OData 路由**：挂载在 `/odata/v1` 前缀下，实体集为 `Photos` 和 `Folders`，元数据地址为 `/odata/v1/$metadata`。
- **REST 接口**：`/api/v1/...`（`Default`、`Photos`、`Folders` 三个控制器），包括上传 `POST /api/v1/Photos/upload` 和目录树 `GET /api/v1/Folders/tree`（见第 3 节）。
- **健康检查**：`/health`，返回 `{"status":"ok","mongo":"ok"}`，会顺带检查 MongoDB 是否可用。
- **原图**：由程序自己的静态文件中间件（`UseStaticFiles`）从 `RootFolder` 提供，路径为 `/File/<相对路径>`，支持 Range 请求，不提供目录列表。
- **Swagger**：使用 **NSwag 14**（`AddOpenApiDocument` + `UseOpenApi` + `UseSwaggerUi`），生成 OpenAPI 3.0 文档。
    - **UI 地址**：`/swagger`（跳转到 `/swagger/index.html`）。
    - **JSON 文档**：`/swagger/v1/swagger.json`。
    - csproj 里虽然引用了 Swashbuckle，但实际没有使用。

## 3. 认证方式
- 密码保存在服务器上的纯文本文件里（位置由 `UserPassLocation` 指定）。**该文件绝不能提交到仓库**，`.gitignore` 中的 `UserPass*.txt` 规则会忽略它。
- 需要权限的接口通过 `userPass` 请求头传递**密码的 MD5**。
- `GET /api/v1/Default/validate?userPass=<明文密码>` 校验密码，成功时返回 MD5，客户端之后用它作为 `userPass` 请求头。
- 不带正确 `userPass` 时，读取照片列表会自动隐藏私密照片。注意：OData 文件夹列表和 `/File/` 原图目前**不需要密码**也能访问。
- 上传和目录树接口在读取请求体之前就检查 `userPass`（`RequireUserPassAttribute`），缺少或错误时直接返回 401。

### 上传：`POST /api/v1/Photos/upload`
- `multipart/form-data`：`folder` 字段为目标目录（如 `2026/香港旅行`，不存在时逐级创建，父目录 id 正确设置），一个或多个 `files` 文件。
- 单次请求最大 1 GB（与 Nginx 的 `client_max_body_size` 一致）。文件边接收边写入同目录下的隐藏临时文件，写完再改名，不会整个读进内存，也不会留下半个文件。
- 支持的格式和 ingest 相同（jpg、jpeg、png、bmp、wav、avi、mp4、3gp）。保存后立即读取 EXIF 拍摄时间、生成缩略图并写入数据库，不需要再调用 ingest。
- 每个文件单独返回结果，部分失败时整个请求仍返回 200：
    - `created`：已保存；`savedAs` 是服务器上的文件名。
    - `duplicate`：目录里已有内容完全相同的文件，未重复保存，`id` 是已有照片的 id。
    - 同名但内容不同的文件会自动改名为 `name (1).jpg`。
    - `rejected`：文件名不合法、格式不支持或空文件；`failed`：保存或读取图片失败（不会留下文件）。
- 目录不合法（为空、包含 `..` 或以 `.` 开头的段、超出照片根目录）时返回 400。

### 目录树：`GET /api/v1/Folders/tree`
- 返回整棵目录树（`FolderNode`：`id`、`path`、`name`、`photoCount`、`totalCount`、`children`），根节点的 `path` 为空字符串，子目录按名称排序。
- `photoCount` 是该目录本身的文件数，`totalCount` 包含所有子目录（含私密照片）。
- 树按数据库中的 `path` 构建，不依赖 `parentFolderId`（旧的 ingest 在三层以上目录时会把它设错）。只用 FTP 传上去、还没 ingest 的目录不会出现。

## 4. 部署与反向代理 (Nginx)
Nginx 作为入口网关，模拟 IIS 虚拟目录行为。配置文件为 `/etc/nginx/sites-enabled/default`，其中有三个 server 块：

1. `listen 80 default_server`（按 IP 访问时使用）。
2. `yww325.eu.org` 的 HTTPS（443），证书为 Let's Encrypt：`/etc/letsencrypt/live/yww325.eu.org/`。
3. `yww325.eu.org` 的 HTTP（80），全部 301 跳转到 HTTPS。

第 1、2 个 server 块中与照片 API 相关的 location 相同：

```nginx
# 虚拟目录模式：自动切除前缀并转发
location ^~ /MyPhotos/ {
    # 添加 CORS 响应头（允许 userPass 请求头）；OPTIONS 预检请求直接返回 204
    proxy_pass http://127.0.0.1:5000/;   # 末尾斜杠负责切掉 /MyPhotos/
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_set_header X-Forwarded-Prefix /MyPhotos;
    # 超时 1200s，client_max_body_size 1G
}
location ^~ /myphotos/  { proxy_pass http://127.0.0.1:5000/; }      # 小写别名
location ^~ /MyPhotos2/ { proxy_pass http://127.0.0.1:5001/; }      # 实例 2（同样设置 X-Forwarded-Prefix /MyPhotos2）

# 原图：转发给对应实例的 /File/ 静态文件中间件（不是 Nginx 直接读目录）
location ^~ /File/  { proxy_pass http://127.0.0.1:5000/File/; }
location ^~ /File2/ { proxy_pass http://127.0.0.1:5001/File/; }

# 其他站点内容的静态文件缓存
location ~* \.(jpg|jpeg|png|gif|ico|css|js|svg|woff|woff2|ttf|eot)$ {
    expires 1y;
    add_header Cache-Control "public, immutable";
    access_log off;
}
```

**location 优先级说明**：`^~` 前缀匹配一旦命中，Nginx 就不再检查正则 location。所以 `/MyPhotos/` 下的 `.css`、`.js`、`.jpg`（例如 Swagger 的 `swagger-ui.css`）会走上面的 `proxy_pass`，不会被底部的静态文件缓存规则拦截。这个效果完全来自 `^~`，正则 location 里没有任何针对 `/MyPhotos/` 的判断。

同一配置中还有 `/stock/`、`/ng-app/`、`/ng-app2/`、`/blog/` 等其他站点，与本项目无关。

## 5. 系统服务 (systemd)
服务以 **systemd 用户服务**的形式运行（用户 `yww`，已开启 linger，开机即自动启动，无需登录）：

- **实例 1**：`~/.config/systemd/user/myphoto5000.service`
    - 运行 `.../MyPhotoWebApi/publish/MyPhotoWebApi --urls http://127.0.0.1:5000`，工作目录为 `publish/`。
    - `Restart=on-failure`，`RestartSec=3`，`WantedBy=default.target`。
- **实例 2**：`myphoto5001.service`，运行 `/home/yww/Coding/MyPhotoWebApi2/publish`，监听 `127.0.0.1:5001`。
- **运行时更新自动重启**：`myphoto-runtime-watch.path` 监视 .NET 运行时目录，一旦有变化，就执行 `/usr/local/bin/restart-on-runtime-change.sh` 重启两个实例。
- **控制命令**（以 `yww` 用户执行，不需要 sudo）：
    - 启动：`systemctl --user start myphoto5000`
    - 重启：`systemctl --user restart myphoto5000`
    - 状态：`systemctl --user status myphoto5000`
    - 日志：`journalctl --user -u myphoto5000 -f`
- 旧的系统级服务 `myphotoapi.service` 已停用，只剩备份文件 `/etc/systemd/system/myphotoapi.service.bak.2026-04-26_025445`。

## 6. 访问路径汇总
域名 `yww325.eu.org` 解析到本机公网 IP `20.214.10.34`。下面的路径对 `https://yww325.eu.org` 和 `http://20.214.10.34` 都适用：

- **Swagger UI**：`/MyPhotos/swagger/index.html`
- **OpenAPI JSON**：`/MyPhotos/swagger/v1/swagger.json`
- **OData 元数据**：`/MyPhotos/odata/v1/$metadata`
- **OData 查询示例**：`/MyPhotos/odata/v1/Folders`、`/MyPhotos/odata/v1/Photos?$top=10`
- **密码校验**：`/MyPhotos/api/v1/Default/validate?userPass=<明文密码>`
- **健康检查**：`/MyPhotos/health`
- **原图**：`/File/<相对路径>`（也可以用 `/MyPhotos/File/<相对路径>`）；实例 2 为 `/File2/<相对路径>`

---
*文档更新日期：2026-10-11*
