# AegisVault（玄钥）

**本地优先、零知识的跨平台密码管理器** — 基于 .NET 10 + Avalonia UI 构建，以金融级加密保护你的数字资产。

[![CI](https://github.com/AtomUICommunity/AegisVault/actions/workflows/ci.yml/badge.svg)](https://github.com/AtomUICommunity/AegisVault/actions/workflows/ci.yml)
[![License: GPL-3.0](https://img.shields.io/badge/License-GPL--3.0-blue.svg)](LICENSE)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)
![Avalonia](https://img.shields.io/badge/Avalonia-12.1-8A2BE2)

## 下载

到 **[Releases](https://github.com/AtomUICommunity/AegisVault/releases/latest)** 获取最新构建：

| 平台 | 文件（`<版本>` 为 tag 名，如 `v0.6.2`） |
|---|---|
| Windows x64（安装包） | `AegisVault-win-x64-setup-<版本>.exe`（推荐，per-user 安装、可在「应用和功能」卸载） |
| Windows x64（便携） | `AegisVault-win-x64-<版本>.zip` |
| Linux x64 | `AegisVault-linux-x64-<版本>.tar.gz` |
| macOS（Apple 芯片） | `AegisVault-osx-arm64-<版本>.tar.gz` |
| macOS（Intel） | `AegisVault-osx-x64-<版本>.tar.gz` |

单文件、无需 .NET 运行时（NativeAOT）。压缩包内含 `LICENSE` 与 `THIRD-PARTY-NOTICES.md`；每个平台另附 **`SHA256SUMS-<rid>`** 校验文件（独立资产，安装包也在校验和清单内）。**卸载不会删除你的库与偏好**。macOS 构建未签名/未公证，首次运行需在「系统设置 → 隐私与安全性」放行；Linux 上需 `chmod +x`，也可用包内 `install.sh` 安装到 `~/.local`。

各版本变更与**升级须知**（含库格式兼容性）见 **[CHANGELOG.md](CHANGELOG.md)**。

## 界面预览

| 主界面（浅色 · 中文） | 主界面（深色 · English） |
|---|---|
| <img src="assets/screenshots/main-light-zh.png" width="480" alt="主界面 浅色 中文"> | <img src="assets/screenshots/main-dark-en.png" width="480" alt="main window dark english"> |

| 首次启动：创建密码库 | 快速访问浮层（`Ctrl+Shift+Space`） |
|---|---|
| <img src="assets/screenshots/unlock-light-zh.png" width="300" alt="首次启动创建密码库"> | <img src="assets/screenshots/quickaccess-light-zh.png" width="480" alt="快速访问浮层"> |

| 密码生成器 | 新建分类：8 预设色板 + 内嵌取色器 |
|---|---|
| <img src="assets/screenshots/generator-light-zh.png" width="420" alt="密码生成器"> | <img src="assets/screenshots/category-colors-light-zh.png" width="380" alt="分类颜色"> |

<sub>截图为演示数据（非真实凭据）；窗口按 100% 缩放采集，浅色/深色主题与中/英界面均受支持。</sub>

## 特性

- 🔐 **零知识加密**：主密码仅用于派生密钥，数据以 AES-256-GCM 逐条认证加密，密码库文件不含任何明文
- 🔄 **版本化数据迁移**：条目/设置负载带版本号并绑定 AAD；旧库解锁时自动归一化并一次性写回升级，旧版应用打开新库会明确报错（设计见 `docs/数据迁移设计.md`）
- 🧮 **现代密钥派生**：Argon2id（64 MiB / 3 轮，盐 16B）派生 KEK；分层密钥体系（KEK 包裹 DEK），改主密码无需全量重加密
- 🧠 **安全内存**：密钥存放于 libsodium 安全内存（`sodium_malloc` + 锁定 + 只读保护），退出/锁定时强制清零
- 📝 **条目管理**：标题/用户名/密码/网址/备注/标签/自定义字段，实时搜索、收藏置顶、预览与编辑分离；详情显示创建时间**与多条网址（逐条可点击打开）**；删除需确认；标题必填、网址自动补全并校验 http/https
- 🗂️ **分类 + 标签双模型**：分类为单归属（新建/重命名/删除/移动/合并，删除后条目转为未分类），标签可多维度交叉；左栏按 📁 分类 / # 标签 区分展示；重名与层级深度均有校验
- 🎨 **现代界面（Ink Green 主题）**：AtomUI 组件 + 设计 Token（盾绿主色、中性灰阶、8pt 间距），平面化三栏布局、检查器式详情、**浅色 / 深色 / 跟随系统 三态切换（设置菜单内切换）**
- 🗂️ **可拖拽的分类树**：拖到行上/下四分之一调整同级顺序、拖到行体成为子分类，同级顺序随库保存；展开/折叠带动画且折叠状态跨重启保持；支持键盘 `←`/`→` 与双击展开收起
- ⏱️ **TOTP 验证码**：RFC 6238（SHA-1/256/512），倒计时展示与一键复制
- 🎲 **密码生成器**：长度/字符集/排除易混淆字符/熵强度预估
- 🔒 **自动锁定**：空闲超时（默认 5 分钟，基于单调时钟计算，不受系统时间调整影响）、最小化、系统锁屏、挂起；锁定即清内存并显示遮罩，一键解锁
- 🖥️ **桌面集成**：系统托盘（打开/快速访问/设置/锁定/退出）、可选悬浮球、最小化到托盘
- ⚡ **快速访问**：`Ctrl+Shift+Space` 全局热键呼出浮层（Windows），搜索 + 键盘复制 + 跳转条目，`Ctrl+Enter` 一键复制密码并打开网址
- 🩺 **安全健康面板**：弱密码、跨条目复用与长期未更新（>1 年）检测，一键过滤问题条目
- 🎯 **密码轮换引导**：详情显示密码年龄，健康面板标记建议轮换的条目；改密时保留最近 10 条历史记录（可查看/复制）
- 📥 **导入**：Bitwarden CSV 与通用格式（自动识别），后台导入
- 📤 **导出**：明文 CSV（二次确认 + 公式注入防护）与口令加密 JSON；采用「临时文件 + 原子替换」写入，不会残留半截明文（Unix 上另设仅本人可读写权限）
- 📋 **剪贴板保护**：复制密文后按配置延时自动清除（仅在内容未被替换时，且仅保留哈希指纹），带倒计时提示与"立即清除"；在 Windows 上文本与「不记入剪贴板历史」标记在**同一次剪贴板事务**内写入，避免被 `Win+V` 历史截获
- 🌐 **多语言**：简体中文 / English（**设置窗口内切换，可一键重启生效**；字符串表静态实现，AOT 安全）
- 🛡️ **防截屏**（Windows）：窗口内容排除在截屏/录屏之外（默认开启，可关闭）
- 💻 **记住设备**（Windows）：DPAPI 保护设备密钥，下次启动免密解锁；更换主密码即自动失效
- 🛡️ **进程硬化**：禁用核心转储（Windows `SetErrorMode` / Unix `setrlimit`）、锁屏/挂起监听
- ⌨️ **安全输入**（Windows，实验性）：系统安全桌面凭据对话框，阻断常规键盘记录
- 🚀 **NativeAOT 发布**：单文件原生可执行（Windows x64 实测约 49 MB），无需安装 .NET 运行时，启动快、内存占用低

## 技术栈

| 组件 | 选型 |
|---|---|
| 运行时 | .NET 10（NativeAOT） |
| UI | Avalonia 12.1.4 + AtomUI 6.2（Ant Design 6 风格组件） |
| MVVM | CommunityToolkit.Mvvm（源生成器，AOT 友好） |
| 存储 | SQLite（Microsoft.Data.Sqlite 10.0.12 + SQLitePCLRaw 2.1.12 / SQLite 3.53.3） |
| 密码学 | libsodium（Sodium.Core 1.4.1，Argon2id）+ BCL AES-256-GCM |
| 平台安全 | Windows DPAPI / CredUI（macOS Keychain、Linux libsecret 预留接口） |
| 测试 | xUnit（653 项：Core / Platform / Headless UI） |

## 快速开始

**前置要求**：.NET SDK 10.0.301+（Windows 上 NativeAOT 发布需要 VS Build Tools 的 C++ 工作负载）

```bash
# 构建
dotnet build -c Release

# 运行（开发）
dotnet run --project src/AegisVault.App

# 测试（653 项）
dotnet test -c Release

# NativeAOT 单文件发布（示例：Windows x64）
dotnet publish src/AegisVault.App/AegisVault.App.csproj -c Release -r win-x64 -p:PublishAot=true
```

首次使用：启动后在解锁窗口选择"创建新密码库"，设置主密码（至少 8 位，建议更强）。**主密码无法找回**，请务必牢记并做好加密备份。

> 参与开发前请先读 [`AGENTS.md`](AGENTS.md)：构建/测试门禁、硬性约束（数据迁移四件套、AtomUI 图标着色、滚动条留白等）与验证方式。

## 项目结构

```
AegisVault/
├─ src/
│  ├─ AegisVault.Core/        # 纯逻辑：密码学、加密 SQLite 存储、领域服务
│  ├─ AegisVault.Platform/    # 平台安全：DPAPI、CredUI、核心转储防护、会话监听
│  └─ AegisVault.App/         # Avalonia + AtomUI 桌面应用（MVVM）
├─ tests/
│  ├─ AegisVault.Core.Tests/      # 密码学 / 存储 / 服务单测（含 RFC 向量）
│  ├─ AegisVault.Platform.Tests/  # DPAPI 等平台能力
│  └─ AegisVault.App.Tests/       # ViewModel 与无头 UI 测试
├─ .github/workflows/         # CI（三平台构建+测试）与 Release（四平台 AOT 打包）
├─ assets/screenshots/        # README 界面截图（浅/深、中/英）
├─ AGENTS.md                  # 开发约定与硬性约束（贡献者/代理必读）
└─ THIRD-PARTY-NOTICES.md     # 第三方组件与许可证全文
```

## 安全设计

```
主密码 ──Argon2id(salt, m=64MiB, t=3)──▶ KEK ──AES-256-GCM──▶ 包裹 DEK
DEK(随机 32B) ──AES-256-GCM(每条目随机 nonce)──▶ 条目密文（AAD 绑定 id+版本）
```

- 头部参数（KDF 参数、盐等）参与 AAD，防降级/篡改；条目密文防换位重放
- 密码库为加密 SQLite 文件（`.aegis`），元数据不含秘密
- 解锁后仅解密到内存；锁定时清零会话密钥、清理剪贴板并关闭敏感窗口
- 复制密码时，文本与「不记入剪贴板历史 / 不上传云剪贴板」标记在同一次剪贴板事务内写入
- 导出文件采用「临时文件 + 原子替换」，不会残留半截明文；Unix 上另设为仅本人可读写
- 空闲锁定基于单调时钟，系统时间调整无法绕过
- 详细审计记录见仓库提交历史与本地 `docs/`（规划、实施方案、可行性验证、多轮代码审计）

**已知局限**：Avalonia 文本输入返回托管字符串，无法主动清零（下个版本评估自定义安全输入控件）；主密码遗忘无法恢复（零知识设计）；设备密钥基于 DPAPI CurrentUser，同一 Windows 账户下的其他进程与浏览器同属一个信任边界。

## 平台支持

| 平台 | 状态 |
|---|---|
| Windows 10/11 | 完整支持（DPAPI 记住设备、CredUI 安全输入、托盘、锁屏监听、防截屏、剪贴板历史排除） |
| Linux | 构建/运行支持；托盘依赖 StatusNotifierItem（GNOME 需扩展）；文件权限强制 0600 |
| macOS | 构建/运行支持；Keychain 集成在路线图中 |

## 路线图

- [x] M0–M7：工程基线、密码学内核、加密存储、核心服务、UI、桌面集成、安全硬化、CI/CD
- [x] P0–P3：解锁重构、三栏主窗口、快速访问/热键/悬浮球/防截屏、健康面板、CSV 导入、i18n 中英
- [x] **v0.2.0 发布**：Windows / Linux / macOS（x64 与 arm64）四平台 NativeAOT 单文件产物
- [x] **v0.3.0 发布**：树形分类（最多 4 级、整枝筛选）、中文界面标题「玄钥」、分类结构不变量修复（移动/合并/删除提升/加载期同父重名）
- [x] **v0.3.1**：复制/显示按钮状态反馈、侧栏「未分类」即时刷新、Windows 发布包平铺解包（未单独发版，并入 v0.3.2）
- [x] **v0.3.2 发布**：父分类可选中查看条目、`Esc`/`Ctrl+1…4` 快捷键、主窗口位置与大小记忆、解锁窗高度微调
- [x] **v0.4.0 发布**：回收站与删除撤销、智能视图「最近」、导出能力（明文 CSV + 口令加密 JSON）
- [x] **v0.4.1**：条目编辑操作栏吸底、「减少动效」开关、关闭窗口选择退出/最小化到托盘、快速访问预热
- [x] **v0.5.0 发布**：本地自动备份与恢复、密码轮换记录、开机启动（macOS/Linux）、Windows 安装包
- [x] **v0.5.1 / v0.5.2**：轮换历史保存修复；AtomUI 6.2.8 + Avalonia 12.1.4 升级
- [x] **v0.6.0 发布**：分类树拖拽排序（同级排序 + 移入成为子分类，顺序随库保存）、展开/折叠动画、折叠状态跨重启保持；自绘扁平树替代 NavMenu；侧栏配色统一到品牌色；选择器显示真实层级
- [x] **v0.6.1 发布**：2026-11 安全审计的三个 P1 —— 备份恢复在库打开状态下可用、CSV 导出公式注入防护、锁屏/休眠锁定改在 UI 线程执行；解锁窗与确认框纳入防截屏
- [x] **v0.6.2 发布**：审计纵深 —— 剪贴板排除标记与文本同事务写入、导出文件原子写出（Unix 另设 0600）、空闲锁定改用单调时钟
- [x] **v0.6.3**：侧栏收拢（语言入口并入设置、主题并入设置菜单）、起始页显示版本号、分类行双击展开/收起
- [x] **v0.6.4**：修复导出 CSV 的公式防护会改写内容（前导单引号往返丢字符）、双击箭头无反应、恢复提示可能串库
- [x] **v0.6.5**：修复自启动到托盘后桌面上残留一圈窗口边框轮廓
- [ ] 加密 JSON 导入、S3/OSS 异地备份与定时同步
- [ ] 切换密码库入口、自动更新
- [ ] macOS Keychain / Linux libsecret 完整实现、代码签名与 macOS 公证、桌面快捷方式
- [ ] 自动填充（增强复制流 → 浏览器扩展）、附件、多库同步

## 许可证

- 本项目采用 **GPL-3.0** 开源，见 [LICENSE](LICENSE)
- 第三方组件（含 LGPL-3.0 的 AtomUI）及完整许可证文本见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)

## 安全披露

请勿在公开 Issue 中提交漏洞细节或真实密码库样本。发现安全问题请通过 GitHub Security Advisories（私有披露）联系维护者。
