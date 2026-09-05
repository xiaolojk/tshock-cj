using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Terraria.ID;

namespace ProgressGifts
{
    /// <summary>礼包触发条件类型。</summary>
    public enum GiftTriggerType
    {
        /// <summary>累计游玩时长（分钟）</summary>
        Playtime,
        /// <summary>击败指定 Boss / NPC（次数）</summary>
        BossKill,
        /// <summary>累计死亡次数</summary>
        Death,
        /// <summary>首次进入服务器</summary>
        FirstJoin,
        /// <summary>事件达成（hardmode=进入困难模式，或自定义事件键）</summary>
        Event,
    }

    public class PluginSettings
    {
        /// <summary>全局自动发放开关（仅对 AutoGrant=true 的礼包生效）</summary>
        public bool EnableAutoGrant = true;

        /// <summary>“可领取”提醒的冷却秒数</summary>
        public int NotifyCooldownSeconds = 90;

        /// <summary>玩家登录时提醒未领取的礼包</summary>
        public bool NotifyOnJoin = true;

        /// <summary>/gift 列表每页显示条数</summary>
        public int ListPageSize = 8;

        /// <summary>领取成功后是否全服公告</summary>
        public bool AnnounceGifts = true;
    }

    /// <summary>礼包中的物品。</summary>
    public sealed class GiftItem
    {
        /// <summary>物品 ID（对应 Terraria ItemID）</summary>
        public int Id;

        /// <summary>数量</summary>
        public int Stack = 1;

        /// <summary>前缀（词缀）ID，0 为无前缀</summary>
        public byte Prefix = 0;
    }

    /// <summary>礼包中的 Buff。</summary>
    public sealed class GiftBuff
    {
        /// <summary>Buff ID（对应 Terraria BuffID）</summary>
        public int Id;

        /// <summary>持续秒数</summary>
        public int Seconds = 600;
    }

    /// <summary>礼包触发条件。</summary>
    public sealed class GiftTrigger
    {
        /// <summary>触发类型</summary>
        [JsonConverter(typeof(StringEnumConverter))]
        public GiftTriggerType Type = GiftTriggerType.Playtime;

        /// <summary>Playtime：达成所需的分钟数</summary>
        public int Minutes = 60;

        /// <summary>BossKill / Death：达成所需的次数</summary>
        public int Count = 1;

        /// <summary>BossKill：目标 NPC 的 netID，留空表示“任意 Boss”</summary>
        public int[] NpcIds = Array.Empty<int>();

        /// <summary>Event：事件键。内置 hardmode（世界进入困难模式）；其他自定义键可通过 /gift setflag 设置</summary>
        public string EventKey = "";
    }

    /// <summary>一个进度礼包定义。</summary>
    public sealed class GiftDefinition
    {
        /// <summary>礼包唯一 ID（用于领取记录存储；修改 ID 会使旧领取记录失联）</summary>
        public string Id = "";

        /// <summary>显示名称</summary>
        public string Name = "";

        /// <summary>描述（领取条件的人话说明）</summary>
        public string Description = "";

        /// <summary>触发条件</summary>
        public GiftTrigger Trigger = new GiftTrigger();

        /// <summary>条件达成后自动发放，无需玩家手动领取</summary>
        public bool AutoGrant = false;

        /// <summary>不在 /gift 列表中显示（一般配合 AutoGrant 使用）</summary>
        public bool Hidden = false;

        /// <summary>赠送金币数额，以铜币计（1000000 铜币 = 1 铂金币）</summary>
        public int Money = 0;

        /// <summary>物品列表</summary>
        public List<GiftItem> Items = new List<GiftItem>();

        /// <summary>Buff 列表</summary>
        public List<GiftBuff> Buffs = new List<GiftBuff>();

        /// <summary>领取时以服务器控制台身份执行的命令；$ACCOUNT$ 会被替换为玩家账号名（仅限信任的管理员配置）</summary>
        public string[] Commands = Array.Empty<string>();
    }

    public class Configuration
    {
        public PluginSettings Settings = new PluginSettings();
        public List<GiftDefinition> Gifts = new List<GiftDefinition>();

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
        };

        public static Configuration Load(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    return Read(path);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[进度礼包] 配置文件解析失败，将重建默认配置：" + ex.Message);
                }
            }

            var def = CreateDefault();
            try { def.Write(path); } catch { /* 写入失败时仍使用内存中的默认配置 */ }
            return def;
        }

        public static Configuration Read(string path)
        {
            // 允许在 JSON 中使用 // 行注释，方便写中文说明
            var sb = new StringBuilder();
            foreach (var line in File.ReadAllLines(path))
            {
                var t = line.TrimStart();
                if (t.StartsWith("//")) continue;
                sb.AppendLine(line);
            }
            return JsonConvert.DeserializeObject<Configuration>(sb.ToString(), JsonSettings) ?? new Configuration();
        }

        public void Write(string path)
        {
            var header =
                "// ProgressGifts 进度礼包配置\n" +
                "// Trigger.Type 可选值：Playtime(游玩分钟数) / BossKill(击败Boss次数) / Death(死亡次数) / FirstJoin(首次进服) / Event(事件)\n" +
                "// Trigger.EventKey：hardmode = 世界进入困难模式；其他自定义键用 /gift setflag <玩家> <键> [值] 设置\n" +
                "// 物品/怪物 ID 可参考 Terraria Wiki 的 Item ID / NPC ID 页面\n" +
                "// 修改配置后使用 /gift reload 重载\n\n";
            File.WriteAllText(path, header + JsonConvert.SerializeObject(this, JsonSettings));
        }

        /// <summary>默认配置：覆盖每种触发类型的示例礼包。</summary>
        public static Configuration CreateDefault()
        {
            return new Configuration
            {
                Gifts = new List<GiftDefinition>
                {
                    new GiftDefinition
                    {
                        Id = "first-join",
                        Name = "新手礼包",
                        Description = "首次进入服务器",
                        Trigger = new GiftTrigger { Type = GiftTriggerType.FirstJoin },
                        Items = new List<GiftItem>
                        {
                            new GiftItem { Id = ItemID.Torch, Stack = 30 },
                            new GiftItem { Id = ItemID.Wood, Stack = 100 },
                            new GiftItem { Id = ItemID.LesserHealingPotion, Stack = 10 },
                            new GiftItem { Id = ItemID.RecallPotion, Stack = 5 },
                        },
                    },
                    new GiftDefinition
                    {
                        Id = "playtime-60",
                        Name = "一小时成长礼包",
                        Description = "累计游玩 60 分钟",
                        Trigger = new GiftTrigger { Type = GiftTriggerType.Playtime, Minutes = 60 },
                        Items = new List<GiftItem>
                        {
                            new GiftItem { Id = ItemID.HealingPotion, Stack = 10 },
                            new GiftItem { Id = ItemID.IronskinPotion, Stack = 5 },
                        },
                        Money = 10000, // 1 金币
                    },
                    new GiftDefinition
                    {
                        Id = "playtime-600",
                        Name = "十小时元老礼包",
                        Description = "累计游玩 600 分钟",
                        Trigger = new GiftTrigger { Type = GiftTriggerType.Playtime, Minutes = 600 },
                        Items = new List<GiftItem>
                        {
                            new GiftItem { Id = ItemID.LifeCrystal, Stack = 2 },
                        },
                        Buffs = new List<GiftBuff>
                        {
                            new GiftBuff { Id = BuffID.Regeneration, Seconds = 600 },
                        },
                        Money = 100000, // 10 金币
                    },
                    new GiftDefinition
                    {
                        Id = "boss-first",
                        Name = "首杀礼包",
                        Description = "击败任意 Boss 一次",
                        Trigger = new GiftTrigger { Type = GiftTriggerType.BossKill, Count = 1 },
                        Items = new List<GiftItem>
                        {
                            new GiftItem { Id = ItemID.HealingPotion, Stack = 10 },
                            new GiftItem { Id = ItemID.ManaPotion, Stack = 5 },
                        },
                        Money = 20000,
                    },
                    new GiftDefinition
                    {
                        Id = "boss-eye-of-cthulhu",
                        Name = "克苏鲁之眼讨伐礼包",
                        Description = "击败克苏鲁之眼",
                        Trigger = new GiftTrigger
                        {
                            Type = GiftTriggerType.BossKill,
                            Count = 1,
                            NpcIds = new[] { (int)NPCID.EyeofCthulhu },
                        },
                        Items = new List<GiftItem>
                        {
                            new GiftItem { Id = ItemID.SuspiciousLookingEye, Stack = 3 },
                        },
                        Money = 50000,
                    },
                    new GiftDefinition
                    {
                        Id = "event-hardmode",
                        Name = "肉山克星礼包",
                        Description = "世界进入困难模式",
                        Trigger = new GiftTrigger { Type = GiftTriggerType.Event, EventKey = "hardmode" },
                        Items = new List<GiftItem>
                        {
                            new GiftItem { Id = ItemID.HealingPotion, Stack = 20 },
                        },
                        Money = 200000, // 20 金币
                    },
                    new GiftDefinition
                    {
                        Id = "death-10",
                        Name = "屡败屡战礼包",
                        Description = "累计死亡 10 次（安慰奖）",
                        Trigger = new GiftTrigger { Type = GiftTriggerType.Death, Count = 10 },
                        Items = new List<GiftItem>
                        {
                            new GiftItem { Id = ItemID.HealingPotion, Stack = 10 },
                            new GiftItem { Id = ItemID.Torch, Stack = 30 },
                        },
                        Money = 5000,
                    },
                },
            };
        }
    }
}
