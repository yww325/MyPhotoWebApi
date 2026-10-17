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
- **REST 接口**：`/api/v1/...`（`Default`、`Photos`、`Folders` 三个控制器），包括上传 `POST /api/v1/Photos/upload`、目录树 `GET /api/v1/Folders/tree`、目录内容 `GET /api/v1/Photos/thumbs` 、删除照片 `DELETE /api/v1/Photos`、移动照片 `POST /api/v1/Photos/move-to` 和删除空目录 `DELETE /api/v1/Folders/empty`（见第 3 节）。
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
- 上传、目录树和删除接口在读取请求体之前就检查 `userPass`（`RequireUserPassAttribute`），缺少或错误时直接返回 401。

### 上传：`POST /api/v1/Photos/upload`
- `multipart/form-data`：`folder` 字段为目标目录（如 `2026/香港旅行`，不存在时逐级创建，父目录 id 正确设置），一个或多个 `files` 文件。
- 单次请求最大 1 GB（与 Nginx 的 `client_max_body_size` 一致）。文件边接收边写入同目录下的隐藏临时文件，写完再改名，不会整个读进内存，也不会留下半个文件。
- 支持的格式和 ingest 相同（jpg、jpeg、png、bmp、wav、avi、mp4、3gp）。保存后立即读取拍摄时间（规则见下方“拍摄时间 dateTaken”）、生成缩略图并写入数据库，不需要再调用 ingest。
- 每个文件单独返回结果，部分失败时整个请求仍返回 200：
    - `created`：已保存；`savedAs` 是服务器上的文件名。
    - `duplicate`：目录里已有内容完全相同的文件，未重复保存，`id` 是已有照片的 id。
    - 同名但内容不同的文件会自动改名为 `name (1).jpg`。
    - `rejected`：文件名不合法、格式不支持或空文件；`failed`：保存或读取图片失败（不会留下文件）。
- 目录不合法（为空、包含 `..` 或以 `.` 开头的段、超出照片根目录）时返回 400。

### 拍摄时间 `dateTaken`
- 上传和 ingest 都按以下顺序确定，都没有时才用入库时间：
    - 照片：EXIF 的 DateTimeOriginal，其次是文件名里的时间。
    - 视频和声音：文件名里的时间，其次是 MP4/QuickTime 文件头的录制时间。文件名和 EXIF 一样是拍摄地的当地时间，所以同一趟旅行的视频和照片落在同一天；文件头的时间是 UTC。
    - 文件名里的时间指 `yyyyMMdd_HHmmss` 或 `yyyyMMdd-HHmmss` 形式，例如 `20210710_115044.mp4`、`VID_20210710_115044.mp4`、`Screenshot_20210710-115044.jpg`。
- EXIF 和文件名里的时间没有时区，按服务器时区（香港时间）换算成 UTC 存入数据库；客户端按香港时间换算回来，就是原来的拍摄时钟时间。
- 这套规则只用于新入库的文件。2026 年 2 月之前入库的视频和没有 EXIF 的照片，存的仍是入库时间，可以从毫秒不为 0 认出来（EXIF 和文件名里的时间精确到秒）。

### 目录树：`GET /api/v1/Folders/tree`
- 返回整棵目录树（`FolderNode`：`id`、`path`、`name`、`photoCount`、`totalCount`、`children`），根节点的 `path` 为空字符串，子目录按名称排序。
- `photoCount` 是该目录本身的文件数，`totalCount` 包含所有子目录（含私密照片）。
- 树按数据库中的 `path` 构建，不依赖 `parentFolderId`（旧的 ingest 在三层以上目录时会把它设错）。只用 FTP 传上去、还没 ingest 的目录不会出现。

### 目录内容（缩略图分页）：`GET /api/v1/Photos/thumbs`
- 参数：`pathPrefix`（目录路径；为空或不传表示根目录）、`top`（每页条数，默认 100，最大 500）、`skip`、`orderBy`、`descending`、`recursive`。
- `recursive` 默认为 `false`，只列这一层；为 `true` 时同时列出所有子目录里的文件，并整体排序、分页（`Trip` 不会匹配同级的 `Trip2`）。
- `orderBy` 可以是 `date`（拍摄时间，默认）、`name`（文件名，不区分大小写）或 `size`（文件大小），区分大小写，其他取值返回 400；`descending` 默认为 `true`。不传新参数时与旧版行为相同（只列这一层，按拍摄时间倒序）。
- 响应头 `X-Total-Count` 是符合条件的文件总数，客户端可以据此显示滚动条和占位，按需分页加载。
- 每项包含 `id`、`fileName`、`mediaType`（`photo`、`video` 或 `sound`）、`path`、`dateTaken`、`thumbnail`（Base64，可能为空）、`isPrivate` 和 `size`（磁盘上的字节数，文件不存在时为空）。
- 文件大小不存数据库，每次从磁盘读取。排序在内存中完成，只为当前页读取缩略图；排序值相同的文件再按文件名、目录和 id 排，翻页不会重复或漏掉。实测 1.7 万个文件全部排序约 0.2 秒/页。
- 不带正确 `userPass` 时不返回私密照片，总数也不计入。

### 删除照片：`DELETE /api/v1/Photos`
- 请求体为照片 id 的 JSON 数组，如 `["6ad1...","6ad2..."]`；需要 `userPass`（缺少或错误时直接返回 401），数组为空时返回 400。
- **永久删除**：先删除磁盘上的文件，成功后再删除数据库记录（缩略图存在记录里，一起删掉）。文件删不掉时保留记录，不会出现“记录没了文件还在”的情况；文件本来就不在时只删记录。
- 每个 id 单独返回结果，部分失败时整个请求仍返回 200：`deleted`、`notFound`（没有这张照片）或 `failed`（什么都没删，`error` 说明原因）。
- 目录本身不会被删除；目录树里的数量按剩下的照片重新统计。

### 移动照片：`POST /api/v1/Photos/move-to`
- 请求体为 JSON：`{"folder":"2026/香港旅行","ids":["6ad1...","6ad2..."]}`。`folder` 为空字符串表示根目录；其他目录不存在时逐级创建（只在真正要移动第一张照片时才创建，全部失败时不会留下新目录）。需要 `userPass`（缺少或错误时直接返回 401），`ids` 为空返回 400，目录不合法返回 400。
- 每张照片先移动磁盘上的文件，再更新数据库记录的 `path`、`tags`，改名时还有 `fileName`。记录的其他字段（包括 `dateTaken`、缩略图、私密标记）都不变，所以按日期的排序不受影响；文件的修改时间也保留。
- 目标目录里已有同名文件或同名记录时，自动改名为 `name (1).jpg`，不会覆盖。
- 每个 id 单独返回结果，部分失败时整个请求仍返回 200：`moved`（`from` 是原目录，`savedAs` 是新文件名；已在目标目录的照片也算 `moved`）、`notFound`（没有这张照片）或 `failed`（照片留在原处，`error` 说明原因，例如磁盘上找不到文件）。
- 旧的 `PATCH /api/v1/Photos/move?folderId=...`（请求体为 id 数组）保持不变：按目录 id 指定目标，不能移到根目录，重名时失败，遇到第一个失败就停止，只返回整体成功或失败（500）。

### 删除空目录：`DELETE /api/v1/Folders/empty?folderPath=...`
- 需要 `userPass`（缺少或错误时直接返回 401）。只删除完全空的目录：数据库里没有照片和子目录，磁盘上的目录里也没有任何文件或子目录（没 ingest 的文件也算）。
- 成功时同时删除磁盘上的目录和数据库里的目录记录，返回 200 和 `{"folder":"...","status":"deleted"}`。
- 不是空目录返回 409，目录不存在返回 404，根目录或不合法的路径返回 400。
- 与上传时创建目录互斥，不会出现删到一半又被上传重新建出来的情况。
- 旧的 `DELETE /api/v1/Folders?folderPath=...` 不变：它删除整个目录分支下所有目录和照片的**数据库记录**，磁盘上的文件和目录都保留，客户端不要用它来删目录。

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
