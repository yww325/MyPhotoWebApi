# MyPhotoWebApi 项目配置架构说明

本项目已成功升级至 **.NET 8.0** 和 **OData 8.x**，并采用“虚拟目录”模式部署于 Nginx 后端。

## 1. 核心架构设计
项目采用**全透明挂载**架构。C# 代码内部不感知外部挂载的具体路径（如 `/MyPhotos`），所有的路径映射均由 Nginx 转发规则和 .NET 中间件的相对路径配置共同完成。这使得同一个程序实例可以轻松挂载到不同的虚拟路径（如 `/MyPhotos2`, `/MyPhotos3`）而无需修改代码。

## 2. 后端配置 (.NET 8.0)
- **监听端口**: `http://localhost:5000`
- **OData 路由**: 挂载在 `/odata` 前缀下。元数据地址为 `/odata/$metadata`。
- **Swagger 配置**: 
    - 使用 Swashbuckle 6.5.0。
    - **JSON 终结点**: 使用相对路径 `v1/swagger.json`。
    - **UI 地址**: `http://localhost:5000/swagger`。
- **关键代码 (Startup.cs)**:
    - 使用 `AddOData` 配置 `EdmModel` 和路由组件。
    - 在 `Configure` 方法中，`app.UseSwaggerUI` 显式注册 `v1` 终结点，确保在虚拟目录环境下路径计算正确。

## 3. 部署与反向代理 (Nginx)
Nginx 作为入口网关，模拟 IIS 虚拟目录行为。

### Nginx 配置片段 (`/etc/nginx/sites-enabled/default`)
```nginx
# 虚拟目录模式：自动切除前缀并转发
location ^~ /MyPhotos/ {
    proxy_pass http://localhost:5000/; # 注意末尾斜杠，它负责切掉 /MyPhotos/
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection keep-alive;
    proxy_set_header Host $host;
    proxy_cache_bypass $http_upgrade;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
    proxy_set_header X-Forwarded-Proto $scheme;
}

# 静态文件拦截优化：确保 /MyPhotos/ 下的资源（如 swagger-ui.css）不被 Nginx 静态缓存规则误截
location ~* \.(jpg|jpeg|png|gif|ico|css|js|svg|woff|woff2|ttf|eot)$ {
    # 逻辑：如果路径包含 /MyPhotos/，则不在此处拦截，由上面的 proxy_pass 处理
    expires 1y;
    add_header Cache-Control "public, immutable";
    access_log off;
}
```

## 4. 系统服务 (systemd)
项目已配置为 Linux 系统服务，确保开机自启和崩溃重启。
- **服务名称**: `myphotoapi.service`
- **控制命令**:
    - 启动: `sudo systemctl start myphotoapi`
    - 重启: `sudo systemctl restart myphotoapi`
    - 状态: `systemctl status myphotoapi`

## 5. 访问路径汇总
- **Swagger UI**: `http://20.214.10.34/MyPhotos/swagger/index.html`
- **OData Metadata**: `http://20.214.10.34/MyPhotos/odata/$metadata`
- **API 验证**: `http://20.214.10.34/MyPhotos/api/v1/Default/validate`
- **静态资源 (File)**: `http://20.214.10.34/File/` (独立代理)

---
*文档更新日期：2026-02-11*
