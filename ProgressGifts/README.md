# ProgressGifts 进度礼包

适用于 **TShock 6.1.0 / Terraria 1.4.5.6 / .NET 9** 的服务器插件：按玩家进度（游玩时长、Boss 讨伐、死亡次数、首次进服、事件达成）自动发放或供玩家手动领取礼包。

## 功能

- **五种触发条件**：`Playtime`（累计游玩分钟数）、`BossKill`（击败指定/任意 Boss 的次数）、`Death`（累计死亡次数，可做安慰奖）、`FirstJoin`（首次进入服务器）、`Event`（事件，内置 `hardmode`，支持自定义键）
- **自动发放 / 手动领取**：可按礼包粒度配置 `AutoGrant`；未自动发放的礼包在达成后聊天提醒，玩家用 `/gift claim` 领取
- **多种奖励类型**：物品（含前缀）、金币（铜计价，自动换算铂/金/银/铜）、Buff、控制台命令（支持 `$ACCOUNT$` 占位符）
- **持久化**：SQLite 存储进度与领取记录，服务器重启不丢失；玩家离线期间管理员仍可操作
- **中英文命令别名**：`/gift` = `/礼包`
- **并发安全**：会话级锁 + 数据库级锁，多玩家并发安全；每个礼包全服仅可领取一次（防刷）

## 安装

1. 构建（或在发布包中获取）`ProgressGifts.dll`
2. 将其复制到服务器 `ServerPlugins/` 目录
3. 启动服务器，`tshock/ProgressGifts/` 下会生成 `config.json`（默认含 7 个示例礼包）与 `progress.sqlite3`
4. 按需编辑 `config.json`，游戏内执行 `/gift reload` 重载

> 服务器已自带 TShock / TSAPI / Newtonsoft.Json / Microsoft.Data.Sqlite，只需拷贝本插件 DLL。

## 命令

| 命令 | 说明 |
| --- | --- |
| `/gift [页码]` | 查看礼包列表与领取状态（绿色 = 可领取） |
| `/gift claim <编号>` | 领取指定编号的礼包 |
| `/gift info <编号>` | 查看礼包详情（物品、金币、Buff） |
| `/gift stats [玩家]` | 查看自己（或指定玩家，管理员）的进度 |
| `/gift reload` | （管理员）重载配置 |
| `/gift grant <玩家> <编号>` | （管理员）无视条件直接发放 |
| `/gift reset <玩家> [编号\|all]` | （管理员）重置领取记录（支持离线玩家） |
| `/gift setflag <玩家> <键> [值]` | （管理员）设置自定义事件标记（支持离线玩家） |

**权限**：`/gift` 基础命令对所有玩家开放（无需配置）；管理子命令（reload / grant / reset / setflag、查看他人进度）需要 `progressgift.admin`。

## 配置示例

```jsonc
{
  "Settings": {
    "EnableAutoGrant": true,       // 全局自动发放开关
    "NotifyCooldownSeconds": 90,   // “可领取”提醒冷却
    "NotifyOnJoin": true,          // 登录时提醒未领取礼包
    "ListPageSize": 8,             // /gift 每页条数
    "AnnounceGifts": true          // 领取后全服公告
  },
  "Gifts": [
    {
      "Id": "boss-eye-of-cthulhu",        // 唯一 ID，改名会丢失旧领取记录
      "Name": "克苏鲁之眼讨伐礼包",
      "Description": "击败克苏鲁之眼",
      "Trigger": {
        "Type": "BossKill",               // Playtime/BossKill/Death/FirstJoin/Event
        "Count": 1,                       // 所需次数
        "NpcIds": [ 4 ]                   // 目标 NPC netID；留空 = 任意 Boss
      },
      "AutoGrant": false,                 // 达成后自动发放（无需 /gift claim）
      "Hidden": false,                    // 不在列表显示（配合 AutoGrant）
      "Money": 50000,                     // 金币，铜计价：100 铜币 = 1 铜币… 1000000 = 1 铂
      "Items": [ { "Id": 70, "Stack": 3, "Prefix": 0 } ],
      "Buffs": [ { "Id": 2, "Seconds": 600 } ],
      "Commands": []                      // 以控制台身份执行，$ACCOUNT$ = 账号名（仅限信任配置）
    }
  ]
}
```

`Trigger` 各字段含义：

- `Playtime`：`Minutes` 达标分钟数
- `BossKill`：`NpcIds`（留空 = 任意 boss）+ `Count` 次数
- `Death`：`Count` 次数
- `FirstJoin`：无参数
- `Event`：`EventKey`，内置 `hardmode`（世界进入困难模式）；自定义键用 `/gift setflag` 设置，值 > 0 视为达成

## 工作原理

- **BossWatcher**：主线程每 ~1/3 秒轮询 `Main.npc`，Boss 掉过血且消失、且当时有玩家存活，判定为被击败（全员团灭导致的撤退不算；多段 Boss 同轮询只记一次）
- **HeartbeatService**：每秒为在线登录玩家累计时长并评估礼包；订阅 `KillMe` 数据包精确计死亡
- **数据落盘**：脏会话最短 60 秒自动保存一次；玩家离线立即落盘；关服 `Dispose` 全量保存

## 构建

```bash
dotnet build -c Release
# 产物：bin/Release/net9.0/ProgressGifts.dll
```

## 数据存储

`tshock/ProgressGifts/progress.sqlite3`，四张表：`pg_accounts`（进度主表）、`pg_boss_kills`、`pg_event_flags`、`pg_claims`。可直接用 SQLite 工具查看。

## 常见问题

- **玩家换了角色名**：进度绑定 TShock 账号（`account_id`），改名不影响
- **Boss 击杀记给了所有在线玩家**：这是设计行为（服务器视角的 Boss 讨伐）；需要个人判定可改用 `Commands` 触发其他插件
- **修改了礼包 `Id`**：旧领取记录按 `Id` 匹配，改 `Id` 等于新礼包（旧记录仍在库中）
