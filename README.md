# Soda Terminal Asset Quick Read

Windows x64 独立资产快读工具；与 OCR 扫描器分别安装和运行。

## 安装与使用

安装器 1.1.0 与 Bridge 1.0.6：固定 GitHub raw 与 contents 两源失败后，使用同一固定 commit 的官方压缩包备用源；压缩包和三个选定文件分别核对大小与 SHA256，不内嵌或再分发协议JSON。三路下载共享一次已验证压缩包；响应头、连续无进展和整次耗时均有上限，可取消。升级优先复用已核验的本地协议，补齐已发布1.0.10完整载荷、卸载身份与Bridge1.0.5服务识别；隔离实际载荷升级和注册失败回滚保持旧版程序、读取结果。安装失败可主动打开官网诊断页，核对有限信息后发送并获得回执，不自动上传。安装成功保留当前版与上一版回退，仅移除完整清单和哈希匹配、无未知文件且未占用的更早程序。普通连接服务无终端窗口，旧服务切换仍需确认和身份重查；未知进程不自动停止。登记写后核对，失败回滚；安装和普通启动不开始采集。这不代表实际用户安装或暖升级验收。安装包仍未签名。

仅使用本仓库 Releases 中具名版本的独立安装器，核对 SHA256。首次安装在用户点击安装后，联网从固定社区 commit 的公开 GitHub 地址下载三份协议输入并核对 SHA256，随后从临时目录事务安装到本机；安装器不内嵌这些JSON或游戏图像。取消、网络或哈希错误不会完成安装。安装和普通启动不开始采集；打开 Soda Terminal 的资产快读入口，连接本机工具、选择接收账户、填写游戏安装目录并阅读风险后，才开始本轮读取。管理员提示用于本地网络驱动。先完成游戏更新与首次着色器编译，完全退出客户端，等待工具就绪后正常启动并登录；就绪后最多读取180秒，收到所需三类响应则提前停止。

检查结果不等于导入。核对同一游戏账户及资产内容后再确认导入。工具不执行游戏内强化、装备或拆解，不模拟鼠标键盘，不改包或重放。

## 风险与局限

非官方第三方工具，未获游戏方书面许可。第三方工具及交互数据处理可能与游戏规则冲突，处罚概率未知，不能保证账号安全。WinDivert 使用 UDP/20501 接收副本，过滤不按进程隔离；不要同时运行多个客户端或其他捕获工具。退出采集关闭句柄，不保证驱动服务卸载；不要删除其他程序共享驱动。

资产结果仅本地保存；官方配置查询需要联网，不上传资产快照、原始流量或登录凭据。用户文件不会进入此源码仓库。已验证范围为国服3.2目标字段，不能保证其他账号、后续版本、完整响应或遗漏语义。未观测资产不得自动删除；潜能按已识别字段同步，未知值保留原记录。

## 构建

需要 Windows x64、MSVC C++ Build Tools、Rust（见 Cargo.toml）、.NET 8 SDK、PowerShell。脚本不会安装全局工具或启动采集。

~~~powershell
.\Build-Probe.ps1 -RunTests
.\Build-Bridge.ps1 -OutputDirectory .\build-new -RunTests
.\Build-AssetQuickReadInstaller.ps1 -BridgeOutputDirectory .\build-new -OutputDirectory .\installer-new -RunTests
~~~

Prepare-Upstream.ps1 从公开固定 commit 下载源码与协议输入并校验 SHA256；Build-Probe 从官方固定版本取得 WinDivert 并校验哈希。已有缓存可使用 -Offline；运行时不更新协议。所有下载仅用于构建，源码投影不包含协议原始JSON、驱动二进制或游戏图像。Build-AssetQuickReadInstaller.ps1 默认用 .NET SDK 生成压缩单EXE安装器（内嵌整理后的运行文件，排除三个协议JSON），不需要NSIS；只有显式指定 -NsisPath 才使用可选NSIS封装。-DotNetPath 可指定已有SDK。桥接使用 ASP.NET Core 8 Kestrel，本机启动协议为 soda-asset-quick-read://launch。Package-AssetQuickRead.ps1 是开发者本地运行包准备脚本，可复制本地协议输入，不能将其结果直接当作公开安装器或公开发行zip。Bridge 合成检查通过 Build-Bridge.ps1 -RunTests 执行；脚本在隔离版本目录运行 --self-test 后将相同产物移动至 companion，不额外复制运行时；Probe 使用 Test-PackagedProbe.ps1，不需要真实账号输入。

构建目录和临时目录可用 SODA_DERIVED_ROOT、SODA_BUILD_TEMP_DIRECTORY 或显式脚本参数指定；公开脚本默认使用检出内 .cache/soda-derived，不依赖作者机器路径。固定 Cargo.lock 的完整 Rust 依赖许可、版权和标准库版权已保存在 installer/Rust-DEPENDENCY-NOTICES.txt 与 installer/Rust-STDLIB-COPYRIGHT.html，并随安装器分发。依赖变更后先准备固定上游，在已缓存依赖下运行 Build-RustDependencyNotices.ps1 -OutputDirectory <全新目录>，审阅并更新这两份文档；安装器会验证 Cargo.lock 哈希，不接受过期通知。修改运行库后须复核并更新源码内固定运行文件哈希，再重新构建。

## 许可与再分发

自有源码为 MIT；固定社区上游源码版权保留在 THIRD-PARTY-LICENSES/UPSTREAM-MIT.txt。MIT 主源码不授权任意游戏数据、图像、商标或素材再分发。protocol hash清单只记录输入身份，不证明游戏权利人的许可；发布包不能自动加入未确认授权的游戏素材。

WinDivert 为 LGPLv3/GPLv2 双许可。本工具选用 LGPLv3 路径；二进制分发必须保留完整许可及对应源码的免费获取入口，详见 NOTICE.md。锁定驱动哈希用于默认安全检查；修改库后需修改源码中的对应哈希并重建应用，不限制为调试修改而逆向工程或重新链接的许可权利。
