# pcl-patcher

解锁 Plain Craft Launcher 2（PCL2）中因 `ModSecret.RestrictMcDownload` 而被**编译期移除**的 MC 下载与整合包安装功能。

PCL2 在 2.99 系列中以 `Public Const RestrictMcDownload As Boolean = True` 声明该开关。VB 编译器会把 `Const` 内联并折叠，因此：

- 常量本身在全库**没有任何指令引用**（实测 0 处），单独改元数据里的 Constant 值不会改变任何运行行为；
- 受影响的功能代码在官方 exe 里**根本不存在**——入口被折叠成「弹提示 + 抛异常」，后续逻辑被裁掉。

所以本工具的工作分两类：改常量（象征性，保证反射读取一致），以及用 IL **重建被删除的方法体**。

## 支持版本

| 版本 | 状态 | 说明 |
|---|---|---|
| 2.99.0.0 | 支持 | 6 处补丁，第 6 处自动跳过（原版本身没有该隐藏逻辑） |
| 2.99.0.1 | 支持 | 6 处补丁全部生效 |

工具**不依赖文件哈希**，全部按 IL 结构特征定位（`ldstr`/`newobj` 序列、`get_*` → `set_Visibility` 邻近关系、方法体指令数、`InitializeComponent` 调用点），因此同一份源码在不同时间编译出的产物也能处理。

## 补丁内容

共 6 处（基于 [dnlib](https://github.com/0xd4d/dnlib) 实现）：

| # | 位置 | 修改内容 |
|---|------|----------|
| 1 | `PCL.ModSecret.RestrictMcDownload` | 常量 `True` → `False`（元数据 Constant blob `01` → `00`） |
| 2 | `PCL.PageDownloadInstall.LoaderInit` | 把编译期折叠出的「关闭下载」假 Loader（`LoaderTask` + 提示文案）替换回真正的 `PCL.ModDownload::DlClientListLoader`，恢复 MC 版本列表下载 |
| 3 | `PCL.PageDownloadModify.LoadInstanceFinished` | `CardMinecraft.Visibility` 由硬编码的 `Collapsed`（`ldc.i4.2`）改回 `Visible`（`ldc.i4.0`） |
| 4 | `PCL.ModModpack.ModpackInstall(String, String, String)` | 重建被编译器裁掉的整合包类型分发（CurseForge / HMCL / MMC / MCBBS / Modrinth / 带启动器压缩包 / 普通压缩包），新增 `ModpackInstallDispatch` 静态方法承载分发 |
| 5 | `PCL.ModModpack.ModpackInstall()` | 重建文件选择对话框入口，选中整合包后调用 3 参数重载安装 |
| 6 | `PCL.PageDownloadLeft..ctor` | **仅 2.99.0.1**：构造函数末尾会隐藏「安装」入口、隐藏「资源」标签并把默认子页改成「模组」。截掉 `InitializeComponent` 之后的隐藏代码，恢复成 2.99.0.0 的形态 |

第 6 处的原始形态（2.99.0.1，23 条指令）在 `InitializeComponent` 之后追加了：

```
callvirt  get_ItemInstall()  → set_Visibility(Collapsed)     隐藏「安装」入口
callvirt  get_LabResource()  → set_Visibility(Collapsed)     隐藏「资源」标签
ldarg.0; ldc.i4.2; stfld PageID                              默认子页改成「模组」
callvirt  get_ItemMod()      → set_Checked(true)
```

补丁后恢复为 8 条指令，与 2.99.0.0 一致。

## 使用

```cmd
patch.exe "Plain Craft Launcher 2.exe" patched.exe
```

- 第一个参数：官方原版 `Plain Craft Launcher 2.exe`
- 第二个参数：输出路径（允许已存在，会被覆盖）
- 也可以把 exe **拖到** `patch.exe` 上，输出为 `<原名> Fix<扩展名>`
- 输入与输出不允许是同一个文件
- 生成后可直接替换原文件运行

**本仓库不提供、也不分发任何 PCL2 的二进制文件（无论原版还是补丁版）。** 请自行从官方渠道获取后用本工具打补丁。

## 构建

需要 Windows + .NET Framework 4.x 自带的 `csc.exe`（系统自带，无需安装 SDK），以及 [dnlib](https://www.nuget.org/packages/dnlib) 的 `net45` 版 `dnlib.dll` 放在仓库根目录。

```cmd
build.cmd
```

输出 `patch.exe`。`dnlib.dll` 会作为托管资源**嵌入**到 exe 内，运行时通过 `AssemblyResolve` 从内存加载，因此发布时只需单个 exe，旁边不用放 DLL。

## 校验方式

补丁器内部按特征匹配，匹配失败会明确报错并中止，不产出半成品。打完补丁后建议这样确认：

1. 运行输出的 exe，进入「下载」页，左侧应能看到「安装」入口与「资源」标签（2.99.0.1 原版这两项被隐藏）
2. 双击整合包文件（`.zip` / `.mrpack`）应弹出安装流程，而不是提示「为遵守相关法律法规…」

## 免责声明

- 本工具仅供学习与研究，请支持原作者。
- 对使用补丁版本产生的一切后果由使用者自行承担。
- PCL2 版本更新后 IL 结构可能变化；特征匹配失败时工具会中止而非产出半成品。

## License

MIT
