# GitHub 发布与自动更新配置

## 1. 仓库地址

当前项目使用：

```text
https://github.com/Dream-Tian/TokenFloat
```

仓库需要保持 Public，客户端才能匿名下载 Release 中的更新清单和安装包。

## 2. 推送普通修改

修改通过本地构建和测试后执行：

```powershell
git add .
git commit -m "描述本次修改"
git push
```

如果 GitHub 要求登录，可使用浏览器授权、GitHub Desktop 或 `gh auth login`。

## 3. 允许工作流发布 Release

进入仓库：

1. **Settings → Actions → General**。
2. 找到 **Workflow permissions**。
3. 选择 **Read and write permissions** 并保存。

普通推送和 Pull Request 会运行 `Build` 工作流，执行零警告构建和 xUnit 测试。

## 4. 发布 1.5.0

先提交 1.5.0 的全部修改，再创建同版本标签：

```powershell
git add .
git commit -m "Release 1.5.0"
git push
git tag v1.5.0
git push origin v1.5.0
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

例如发布 `1.6.0`：

```powershell
.\scripts\set-version.ps1 1.6.0
git add .
git commit -m "Release 1.6.0"
git push
git tag v1.6.0
git push origin v1.6.0
```

必须先上传版本提交，再推送同版本标签。稳定的 `releases/latest/download/update-manifest.json` 地址不需要修改。

## 7. 建议的远程仓库设置

- **Settings → General → Features**：开启 Issues。
- **Settings → Branches**：为 `main` 添加保护规则，要求 Pull Request 和 `Build` 状态检查通过。
- **Settings → Actions → General**：保留工作流的 Read and write permissions，供 Release 上传文件。

项目暂未附带开源许可证；选择 MIT、GPL-3.0 或保留所有权利后再单独添加，不要直接套用不确定的许可证。
