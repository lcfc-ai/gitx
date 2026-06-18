# GitX — 分支对比与选择性合并工具

> 弥补 TortoiseGit 缺失的「与其他分支比较」能力，实现类似 IDEA 的分支对比 + 文件选择性覆盖。

## 功能特性

- **右键菜单集成**：在 Git 仓库目录右键 →「GitX - 和其他分支比较」
- **分支列表**：展示本地/远程分支，支持搜索过滤，当前分支置灰不可选
- **过滤型目录树**：仅显示有差异的文件和目录，无变更文件自动隐藏，避免信息噪音
- **双栏代码对比**：上栏 = 目标分支代码，下栏 = 本地当前工作区代码，滚动同步，自动语法高亮
- **选择性覆盖**：支持单个文件、整个文件夹、全部差异文件三种覆盖模式，覆盖前弹窗确认
- **日志缓存**：SQLite 记录对比历史和覆盖操作，内存缓存加速重复查看
- **崩溃保护**：全局异常捕获，崩溃日志自动导出到桌面

## 技术栈

- **.NET 8 WPF** — 桌面 UI
- **LibGit2Sharp** — Git 操作（分支读取、TreeDiff、Blob 读取）
- **AvalonEdit** — 双栏代码编辑器 + 语法高亮
- **CommunityToolkit.Mvvm** — MVVM + Source Generator
- **Microsoft.Data.Sqlite** — 本地日志数据库

## 快速开始

### 编译

```bash
cd GitX
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

输出：`bin/Release/net8.0-windows/win-x64/publish/GitX.exe`

### 注册右键菜单

1. 编辑 `RegisterGitXMenu.reg`，将 `C:\Tools\GitX\GitX.exe` 替换为实际路径
2. 双击运行注册表文件

### 卸载

双击运行 `UninstallGitXMenu.reg`，然后删除程序文件夹即可。

## 项目结构

```
GitX/
  App.xaml               # 全局样式、颜色、转换器
  App.xaml.cs            # 启动逻辑、参数解析、服务初始化
  Core/
    Models/              # BranchModel, DiffTreeModel
    Helpers/             # Converters（Bool/Visibility/Icon）
  Services/
    GitService.cs        # LibGit2Sharp 封装
    DiffService.cs       # 差异树构建
    FileService.cs       # 本地文件覆盖
    CacheLogService.cs   # SQLite 日志 + 内存缓存
  ViewModels/
    BranchSelectViewModel.cs
    MainWindowViewModel.cs
  Views/
    BranchSelectWindow.xaml    # 分支选择弹窗
    MainWindow.xaml            # 主窗口（目录树 + 双栏编辑器）
```

## 使用流程

1. 在 Git 仓库目录右键 →「GitX - 和其他分支比较」
2. 选择要对比的目标分支（本地或远程）
3. 左侧浏览差异文件树，右侧查看双栏代码对比
4. 右键文件/文件夹或点击顶部按钮进行覆盖下载
5. 覆盖完成后，在 IDE 中查看变更并提交

## 注意事项

- 覆盖操作直接写入本地物理文件，**不执行 git merge/commit**，请确保覆盖后在 IDE 中 review 变更
- 文本文件以 UTF-8(无 BOM) 写入；二进制文件通过 Blob 流式复制字节，保证不损坏
- 程序启动时自动 `git fetch`（默认 30s 超时，超时回退本地缓存），「刷新」按钮会重新 Fetch 后再计算差异
- 当前分支在分支列表中置灰且不可选
- 日志数据库位于 `%AppData%\GitX\log.db`

## License

MIT
