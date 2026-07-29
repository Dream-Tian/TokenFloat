# GitHub 首次发布与自动更新配置

## 1. 创建仓库

1. 登录 <https://github.com>，右上角选择 **New repository**。
2. Repository name 建议填写 `TokenFloat`。
3. Public 或 Private 均可；私有仓库的 Release 下载需要登录，普通客户端无法直接自动更新，因此需要自动更新时建议使用 Public。
4. 不要勾选初始化 README、`.gitignore` 或 License，本地项目已经包含这些文件。
5. 创建仓库后复制 HTTPS 地址，例如：

   ```text
   https://github.com/your-name/TokenFloat.git
   ```

## 2. 首次提交

在项目根目录执行：

```powershell
git add .
git commit -m "Initial release"
git remote add origin https://github.com/your-name/TokenFloat.git
git push -u origin main
```

如果 GitHub 要求登录，可使用浏览器授权、GitHub Desktop 或 `gh auth login`。

## 3. 允许工作流发布 Release

进入仓库：

1. **Settings → Actions → General**。
2. 找到 **Workflow permissions**。
3. 选择 **Read and write permissions** 并保存。

普通推送会运行 `Build` 工作流，只检查项目能否以零警告构建。

## 4. 发布第一个版本

项目已经发布过 `1.4.0`；当前默认更新源改动应发布为 `1.4.1`：

```powershell
git tag v1.4.1
git push origin v1.4.1
```

标签会触发 `Release` 工作流，自动完成：

1. 发布 Windows x64 自包含程序。
2. 生成中文 Inno Setup 安装包。
3. 计算 SHA-256。
4. 生成 `update-manifest.json`。
5. 创建 GitHub Release 并上传三个文件。

## 5. 配置 TokenFloat 更新源

项目已经内置以下默认更新源：

```text
https://github.com/Dream-Tian/TokenFloat/releases/latest/download/update-manifest.json
```

第一个 Release 成功后，在托盘中选择 **检查更新...** 验证即可，不需要再次填写。托盘中的 **设置更新源...** 可用于覆盖默认地址。

## 6. 发布后续版本

例如发布 `1.4.1`：

```powershell
.\scripts\set-version.ps1 1.4.1
git add TokenFloat\TokenFloat.csproj installer\TokenFloat.iss
git commit -m "Release 1.4.1"
git push
git tag v1.4.1
git push origin v1.4.1
```

必须先上传版本提交，再推送同版本标签。稳定的 `releases/latest/download/update-manifest.json` 地址不需要修改。
