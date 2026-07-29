# DF60A1

某横板过关游戏（客户端 `1.0.1.9`）本地模拟端研究项目。模拟端使用 `.NET Framework 4.8.1 / x86` 和 SQLite；客户端协议通过静态 IDA 分析及本机实测复原，未修改客户端二进制。

本仓库只包含模拟端源码，不包含客户端、`Script.pvf`、IDA 数据库、NPK、音乐或其他客户端资源。

## 放置目录和 PVF

请将整个 `DF60A1` 仓库目录放到客户端根目录下，并自行把与客户端匹配的 `Script.pvf` 放进仓库的 `data` 目录：

```text
客户端根目录/
  DF60A1/
    DF60A1.sln
    data/
      Script.pvf              用户自行提供，不提交到 Git
    src/
      DF60A1/
        DF60A1.csproj
```

模拟端默认从 `DF60A1/data/Script.pvf` 读取频道脚本，从仓库上一级目录查找客户端主程序。缺少 PVF 时服务端会明确报错并退出。

## 当前可运行进度

截至 2026-07-29，已经完成以下最小闭环：

1. 使用 13 段参数直接启动客户端，不依赖 `DebugConfig.txt`。
2. 完成入口握手、频道列表和频道登录。
3. 使用 SQLite 保存账号及角色。
4. 支持创建角色，角色名、职业和等级能在角色卡上正确显示。
5. 支持选择角色并进入赛丽亚房间。
6. 城镇地图、玩家角色、赛丽亚、UI、BGM 和 NPC 语音正常。
7. 支持首次登录自动进入新手教程副本。
8. 教程地图、玩家、4 只哥布林和战斗逻辑均能加载；客户端已实际上报怪物死亡事件。

当前测试账号：

```text
账号: create_channel_2008
密码: test
服务端: 127.0.0.1:7001
```

## 启动方式

首次运行先在仓库根目录构建 Release/x86：

```powershell
dotnet build .\DF60A1.sln -c Release -p:Platform=x86
```

无参数启动模拟端时，默认监听 `127.0.0.1:7001`：

```powershell
.\src\DF60A1\bin\x86\Release\net481\DF60A1.exe
```

使用 `--host` 和 `--port` 自定义监听 IP、端口，例如：

```powershell
.\src\DF60A1\bin\x86\Release\net481\DF60A1.exe --host 192.168.1.10 --port 7002
```

客户端原始启动参数必须恰好包含 13 段，以 `?` 分隔：

```text
99?服务器IP?端口?账号?密码?0?0?0?0?0?0?0?0
```

也可让模拟端主程序代为组装同样的 13 段参数并启动客户端：

```powershell
.\src\DF60A1\bin\x86\Release\net481\DF60A1.exe --launch --host 127.0.0.1 --port 7001 --account create_channel_2008 --password test
```

`--launch` 只启动客户端，不会同时启动服务端；应先在另一个终端运行模拟端主程序。

## 新手教程实现

客户端收到通知 136 后会执行首次教程启动流程：

```text
notification 136
  -> command 15 ENTER_SELECT_DUNGEON
  -> command 16 SELECT_DUNGEON
  -> command 153 激活模块确认
```

命令 16 的业务载荷为：

```text
u16 dungeon_id
u8  difficulty
u8  option
```

客户端首次选择的是 `dungeon_id=10000`。研究时使用的 2008 国服 `Script.pvf` 中的数据为：

```text
dungeon 10000 = dungeon/Tutorial/Tutorial.dgn
start room     = (0, 0)
map 61000      = map/Tutorial/tutorial.map
minimum level  = 1
monster rows   = 4 x monster.lst id 1
```

模拟端处理命令 15 时发送：

```text
notification 3  USER_STATE
notification 26 UDP_HOST
notification 27 ENTER_SELECT_DUNGEON
```

处理命令 16 时发送：

```text
notification 28 DUNGEON_INFO
notification 29 START_MAP
```

客户端加载地图后发送命令 40，模拟端回复成功并发送空载荷通知 30，完成加载。

相关 2008 客户端静态位置：

```text
notification 3  handler: 0x41B0D3
notification 26 handler: 0x41F144
notification 27 handler: 0x41F16C
notification 28 handler: 0x41F550
notification 29 handler: 0x41F7D3
notification 136 branch : 0x42951C
command 15 send          : 0x42958C
command 16 send          : 0x4295D6
module bit 30 activation : 0x4295DB
```

## 自测

在项目根目录运行：

```powershell
.\src\DF60A1\bin\x86\Release\net481\DF60A1.exe --self-test
```

当前验证结果：

```text
Build: 0 warnings, 0 errors
SELF-TEST OK
```

数据库位置：

```text
src\DF60A1\bin\x86\Release\net481\data\***.sqlite
```

持续协议日志位置：

```text
src\DF60A1\bin\x86\Release\net481\logs\server.log
```

## 项目结构

```text
DF60A1.sln                       模拟端解决方案
data/
  请将Script.pvf放在这里.txt     PVF 放置说明
  Script.pvf                     用户自行提供，已被 Git 忽略
src/DF60A1/
  DF60A1.csproj                  .NET Framework 4.8.1/x86 项目
  EntranceProtocol.cs           入口和频道前置握手
  Protocol.cs                   包头、CRC、上下行加解密
  GamePackets.cs                2008 客户端数据包构造与解析
  GameServer.cs                 TCP 会话及命令分发
  SqliteGameStore.cs            SQLite 账号、角色和坐标存储
  PvfArchive.cs                 PVF 文件读取
  SelfTest.cs                    离线协议和数据库自测
```

## 早期协议逆向记录

### 13 段启动逻辑

IDA 中的 `sub_40BE30` 使用 `?` 分隔原始启动数据。少于 13 段时设置调试标志；调试标志打开时，`DebugConfig.txt` 存在就调用 `sub_4086F0` 载入离线角色，不存在则读取资源字符串 101 并显示从请从某程序启动的错误。

当第 1 段为 `99` 时，第 2、3 段直接写入服务器 IP 和端口；第 4、5 段是账号和密码，其余字段目前使用兼容占位值。

### 创建角色页面

客户端 1.0.1.9 的角色选择/创建模块 RTTI 为 `CNEntranceModule`：

- `sub_899740` 清空角色列表；零角色时将当前槽设为 `-1`，但启用创建按钮。
- `sub_89A550` 在 `USERINFO mode 2` 读取完成后把 `manager+0x5C`（列表完成标志）设为 `1`。
- `sub_8999A0` 在 `manager+0x8C` 创建左下角按钮，客户端坐标为 `(61,417)`。
- `sub_898E90` 在角色数小于 12、列表完成且无待处理请求时，从角色列表切换到创建面板；进入创建页面本身不发送网络包。
- 同一函数 `0x8995AC` 的提交分支构造命令 5：`byte job + uint32 nameLength + nameBytes`。职业为 `0..4`，名字为客户端本地编码字节。
- `sub_40F290` 的命令响应 case 5 位于 `0x40FB43`。成功只读取一个非零成功字节；失败再读取一个错误码字节。

空角色账号应收到固定列表负载 `02 00 00`。创建成功后先发送 `CREATE_CHARACTER ACK`，再发送最新的 `USERINFO subtype 2` 角色列表，使客户端退出创建面板时刷新槽位数据。

### 下行字节变换

2008 客户端的服务端下行字节变换必须按接收方向取逆。`sub_40D6D0` 复制六字节包头后的线上负载，再调用 `sub_776F80(payload, length, 0)`；该函数执行：

```text
plain = ROL6(cipher) XOR 0xB5
cipher = ROR6(plain XOR 0xB5)
```

空角色负载 `02 00 00` 的线上密文应为 `DE D6 D6`。如果直接把客户端解码表达式当作编码，登录成功字节可能仍被解释为非零，但 `USERINFO` 的 mode 不再是 `2`，`CNEntranceModule+0x5C` 不会置位，表现为创建按钮可见却点击无效。

### 频道握手

频道握手不能直接复用 55 客户端布局。2008 客户端的 `sub_419420 case 1` 把 `CHANNEL_INFO` 负载读取为 26 字节配置记录：两个 `uint32`、两个 `byte`、一个 `uint32`、地址记录数量、可变地址记录、最后两个 `uint32`。最小本地配置为 26 个零。55 客户端使用的“前 10 字节为零、offset 10 写入 16 字节 challenge”会把 challenge 误读成配置整数，客户端收到频道包后不会继续发送 `LOGIN`。

`sub_40F290 case 1` 的登录成功分支在成功标志后还会读取四个 `byte`、三个 `uint32` 和两个 `byte`，所以成功响应体必须至少为 19 字节。只返回 `01 00000000` 会使客户端包读取器越过声明的负载边界。模拟端当前将 18 字节初始化字段置零；字段语义继续按 2008 国服客户端确认，不照搬 86 版本已经扩展的端口和 DSTR 布局。

### 安全卡命令 161

命令 161 来自 `CNAntiBotSystem::vftable` 的 `sub_8C74F0`：负载为 `uint32 length + opaque bytes`，登录后立即上传一次，之后约每 60 秒上传 12 字节状态。`sub_40F290` 及通知分派均没有 161 消费分支，模拟端只接收并记录，不发送伪造成功应答。

早期命令号和部分角色/城镇包布局参考了 55 级模拟端及 `df_game_r.i64` 的符号流程，但服务端下行变换、频道布局和最终字段均以 2008 客户端 1.0.1.9 的静态分析及实测为准。入口实测依次发送协议 11、5、9、1；`sub_894B10` 确认频道元数据为 `组数 + 组名[20] + 频道数 + 频道记录`。客户端字符串表的 `china_90` 映射内部组号 99，对应启动参数首段与 PVF 中的测试服组 99。未知包会保留完整十六进制日志，后续逐项确认。

## 已知限制和下一步

- 当前只实现教程的第一个房间，没有换房、通关结算、奖励和返回城镇。
- 命令 42（怪物死亡）已经能收到，但尚未据此广播死亡、掉落或开启后续路径门。
- 命令 160 是教程期间的压缩状态/输入上报，目前只记录日志。
- 命令 153 当前按模块激活确认返回成功。
- 背包、装备、技能、任务、金币、商店、队伍和地下城持久化仍是最小占位。
- 安全卡命令 161 只接收，不伪造服务端业务响应。
- 通知 136 同时承担教程启动和模块 bit 30 激活。将来若增加“跳过教程直接留在城镇”，需要继续确认官方的非教程模块激活路径。
- 当前所有包布局以这份 2008 国服客户端实测为准；不能直接照搬 55 台服或 86 国服的扩展字段。

建议下一阶段优先处理命令 42，使 4 只教程哥布林死亡后能够打开路径并推进教程流程。
