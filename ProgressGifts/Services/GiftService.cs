using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using TShockAPI;
using ProgressGifts.Data;

namespace ProgressGifts.Services
{
    /// <summary>
    /// 进度礼包核心服务：会话管理、进度事件、礼包评估、自动发放与手动领取。
    /// </summary>
    public sealed class GiftService
    {
        /// <summary>在线玩家的会话数据。所有跨线程访问都应持有 Sync 锁。</summary>
        public sealed class PlayerSession
        {
            public readonly object Sync = new object();
            public int AccountId;
            public string AccountName;
            public PlayerProgress Progress;
            public HashSet<string> Claims;
            public bool JoinHandled;
            public bool JoinNotified;
            public DateTime LastSaveUtc = DateTime.UtcNow;
            public readonly Dictionary<string, DateTime> LastNotify = new Dictionary<string, DateTime>();
            public bool Dirty;
        }

        public enum GiftState { Claimed, Claimable, InProgress }

        public sealed class GiftEvaluation
        {
            public GiftDefinition Gift;
            public GiftState State;
            public int Current;
            public int Required;
            public string ProgressText = "";
        }

        private readonly ProgressGiftPlugin _plugin;
        private readonly ConcurrentDictionary<int, PlayerSession> _sessions =
            new ConcurrentDictionary<int, PlayerSession>();

        public GiftService(ProgressGiftPlugin plugin)
        {
            _plugin = plugin;
        }

        // ---------------- 会话 ----------------

        public PlayerSession GetSession(TSPlayer ts)
        {
            int id = ts.Account.ID;
            return _sessions.GetOrAdd(id, _ => LoadSession(ts));
        }

        private PlayerSession LoadSession(TSPlayer ts)
        {
            var p = _plugin.Database.LoadOrCreate(ts.Account.ID, ts.Account.Name);
            var s = new PlayerSession
            {
                AccountId = ts.Account.ID,
                AccountName = ts.Account.Name,
                Progress = p,
                Claims = _plugin.Database.GetClaimIds(ts.Account.ID),
            };
            // 读取离线期间写入的 boss/事件数据
            var full = _plugin.Database.LoadFull(ts.Account.ID);
            if (full != null)
            {
                foreach (var kv in full.BossKills) p.BossKills[kv.Key] = kv.Value;
                foreach (var kv in full.EventFlags) p.EventFlags[kv.Key] = kv.Value;
            }
            return s;
        }

        /// <summary>每秒调用：计时、首见处理、困难模式标记、礼包检查。</summary>
        public void TickSession(TSPlayer ts)
        {
            var s = GetSession(ts);
            var p = s.Progress;

            lock (s.Sync)
            {
                if (!s.JoinHandled)
                {
                    s.JoinHandled = true;
                    p.FirstJoinSeen = true;
                    s.Dirty = true;
                }

                p.TotalSecondsOnline++;
                if (Main.hardMode && !p.HardmodeReached)
                {
                    p.HardmodeReached = true;
                    s.Dirty = true;
                }
                s.Dirty = true;
            }

            if (_plugin.Config.Settings.NotifyOnJoin && !s.JoinNotified)
            {
                s.JoinNotified = true;
                if (HasClaimable(s))
                {
                    ts.SendInfoMessage("[进度礼包] 你有可领取的礼包，输入 /gift（/礼包）查看。");
                }
            }

            CheckGifts(ts, s);
        }

        /// <summary>每秒调用：保存脏会话、清理已离线玩家的会话。</summary>
        public void FlushPassiveSaves(HashSet<int> onlineAccountIds)
        {
            foreach (var s in _sessions.Values)
            {
                if (!onlineAccountIds.Contains(s.AccountId))
                {
                    // 已离线：落盘并移除会话
                    if (s.Dirty || (DateTime.UtcNow - s.LastSaveUtc).TotalSeconds > 30)
                    {
                        SaveSession(s);
                    }
                    _sessions.TryRemove(s.AccountId, out _);
                }
                else if (s.Dirty && (DateTime.UtcNow - s.LastSaveUtc).TotalSeconds >= 60)
                {
                    SaveSession(s);
                }
            }
        }

        public void Shutdown()
        {
            foreach (var s in _sessions.Values)
            {
                SaveSession(s);
            }
            _sessions.Clear();
        }

        private void SaveSession(PlayerSession s)
        {
            lock (s.Sync)
            {
                _plugin.Database.Save(s.Progress);
            }
            s.Dirty = false;
            s.LastSaveUtc = DateTime.UtcNow;
        }

        // ---------------- 进度事件 ----------------

        public void RegisterDeath(TSPlayer ts)
        {
            var s = GetSession(ts);
            lock (s.Sync)
            {
                s.Progress.DeathCount++;
                s.Dirty = true;
            }
            CheckGifts(ts, s);
        }

        /// <summary>一次 Boss 击杀，记给所有在线且已登录的玩家。</summary>
        public void RegisterBossKill(int netId)
        {
            foreach (var ts in TShock.Players)
            {
                if (ts == null || !ts.Active || !ts.IsLoggedIn || ts.Account == null) continue;
                var s = GetSession(ts);
                lock (s.Sync)
                {
                    s.Progress.BossKills.TryGetValue(netId, out int n);
                    s.Progress.BossKills[netId] = n + 1;
                    s.Dirty = true;
                }
                CheckGifts(ts, s);
            }
        }

        /// <summary>管理员设置自定义事件标记。</summary>
        public void SetEventFlag(int accountId, string key, int value, TSPlayer onlineTarget)
        {
            if (onlineTarget != null && onlineTarget.Active && onlineTarget.IsLoggedIn)
            {
                var s = GetSession(onlineTarget);
                lock (s.Sync)
                {
                    s.Progress.EventFlags[key] = value;
                    s.Dirty = true;
                }
                CheckGifts(onlineTarget, s);
                return;
            }

            _plugin.Database.SetEventFlag(accountId, key, value);
            if (_sessions.TryGetValue(accountId, out var cached))
            {
                lock (cached.Sync)
                {
                    cached.Progress.EventFlags[key] = value;
                    cached.Dirty = true;
                }
            }
        }

        /// <summary>管理员重置领取记录；giftId 为 null 表示全部。</summary>
        public void AdminReset(int accountId, string giftId)
        {
            if (giftId == null)
            {
                _plugin.Database.RemoveAllClaims(accountId);
                if (_sessions.TryGetValue(accountId, out var s))
                {
                    lock (s.Sync) s.Claims.Clear();
                }
            }
            else
            {
                _plugin.Database.RemoveClaim(accountId, giftId);
                if (_sessions.TryGetValue(accountId, out var s))
                {
                    lock (s.Sync) s.Claims.Remove(giftId);
                }
            }
        }

        // ---------------- 评估 ----------------

        public GiftEvaluation Evaluate(GiftDefinition g, PlayerSession s)
        {
            var e = new GiftEvaluation { Gift = g, Required = 1 };
            lock (s.Sync)
            {
                var p = s.Progress;
                switch (g.Trigger.Type)
                {
                    case GiftTriggerType.FirstJoin:
                        e.Current = 1;
                        e.Required = 1;
                        e.ProgressText = "首次进入服务器";
                        break;

                    case GiftTriggerType.Playtime:
                        e.Required = Math.Max(1, g.Trigger.Minutes);
                        e.Current = (int)(p.TotalSecondsOnline / 60);
                        e.ProgressText = FormatProgress(e.Current, e.Required, "分钟");
                        break;

                    case GiftTriggerType.BossKill:
                        e.Required = Math.Max(1, g.Trigger.Count);
                        if (g.Trigger.NpcIds.Length > 0)
                        {
                            e.Current = g.Trigger.NpcIds
                                .Sum(id => p.BossKills.TryGetValue(id, out var n) ? n : 0);
                        }
                        else
                        {
                            e.Current = p.TotalBossKills;
                        }
                        e.ProgressText = e.Current + "/" + e.Required + " 次";
                        break;

                    case GiftTriggerType.Death:
                        e.Required = Math.Max(1, g.Trigger.Count);
                        e.Current = p.DeathCount;
                        e.ProgressText = e.Current + "/" + e.Required + " 次";
                        break;

                    case GiftTriggerType.Event:
                        bool ok = IsEventSatisfied(g.Trigger.EventKey, p);
                        e.Current = ok ? 1 : 0;
                        e.Required = 1;
                        e.ProgressText = DescribeEvent(g.Trigger.EventKey);
                        break;
                }

                if (s.Claims.Contains(g.Id))
                {
                    e.State = GiftState.Claimed;
                }
                else if (e.Current >= e.Required)
                {
                    e.State = GiftState.Claimable;
                }
                else
                {
                    e.State = GiftState.InProgress;
                }
            }
            return e;
        }

        private static bool IsEventSatisfied(string key, PlayerProgress p)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (key.Equals("hardmode", StringComparison.OrdinalIgnoreCase)) return p.HardmodeReached;
            return p.EventFlags.TryGetValue(key, out var v) && v > 0;
        }

        private static string DescribeEvent(string key)
        {
            if (string.IsNullOrEmpty(key)) return "未知事件";
            if (key.Equals("hardmode", StringComparison.OrdinalIgnoreCase)) return "世界进入困难模式";
            return "事件：" + key;
        }

        private static string FormatProgress(int cur, int need, string unit)
        {
            if (need >= 120) return (cur / 60) + "/" + (need / 60) + " 小时";
            return cur + "/" + need + " " + unit;
        }

        public bool HasClaimable(PlayerSession s)
        {
            foreach (var g in _plugin.Config.Gifts)
            {
                if (string.IsNullOrEmpty(g.Id)) continue;
                if (Evaluate(g, s).State == GiftState.Claimable) return true;
            }
            return false;
        }

        // ---------------- 检查 / 自动发放 / 提醒 ----------------

        public void CheckGifts(TSPlayer ts, PlayerSession s)
        {
            var cfg = _plugin.Config;
            foreach (var g in cfg.Gifts)
            {
                if (string.IsNullOrEmpty(g.Id)) continue;

                var e = Evaluate(g, s);
                if (e.State != GiftState.Claimable) continue;

                if (g.AutoGrant && cfg.Settings.EnableAutoGrant)
                {
                    TryClaim(ts, s, g, "auto");
                }
                else
                {
                    lock (s.Sync)
                    {
                        var now = DateTime.UtcNow;
                        s.LastNotify.TryGetValue(g.Id, out var last);
                        if ((now - last).TotalSeconds >= Math.Max(10, cfg.Settings.NotifyCooldownSeconds))
                        {
                            s.LastNotify[g.Id] = now;
                            ts.SendInfoMessage("[进度礼包] 「{0}」已可领取！输入 /gift 查看并领取。", g.Name);
                        }
                    }
                }
            }
        }

        // ---------------- 领取与发放 ----------------

        /// <summary>尝试领取。source: manual / auto / admin。admin 可无视达成条件。</summary>
        public bool TryClaim(TSPlayer ts, PlayerSession s, GiftDefinition g, string source)
        {
            lock (s.Sync)
            {
                if (s.Claims.Contains(g.Id)) return false;

                if (source != "admin")
                {
                    var e = Evaluate(g, s);
                    if (e.State != GiftState.Claimable) return false;
                }

                s.Claims.Add(g.Id); // 先占用，防止并发重复领取
            }

            bool granted;
            try
            {
                Grant(ts, g);
                granted = true;
            }
            catch
            {
                lock (s.Sync) s.Claims.Remove(g.Id);
                throw;
            }

            _plugin.Database.SetClaim(s.AccountId, g.Id, source);

            if (source == "auto")
            {
                ts.SendSuccessMessage("[进度礼包] 达成条件，已自动发放「{0}」！", g.Name);
            }
            else
            {
                ts.SendSuccessMessage("[进度礼包] 成功领取「{0}」！", g.Name);
            }

            if (_plugin.Config.Settings.AnnounceGifts)
            {
                TSPlayer.All.SendMessage(
                    string.Format("[进度礼包] {0} 领取了「{1}」！", ts.Name, g.Name),
                    new Microsoft.Xna.Framework.Color(255, 200, 60));
            }
            return granted;
        }

        /// <summary>实际发放奖励内容。</summary>
        public void Grant(TSPlayer ts, GiftDefinition g)
        {
            foreach (var it in g.Items)
            {
                if (it == null || it.Id <= 0 || it.Id >= ItemID.Count || it.Stack <= 0) continue;
                ts.GiveItem(it.Id, it.Stack, it.Prefix);
            }

            GiveCoins(ts, g.Money);

            foreach (var b in g.Buffs)
            {
                if (b == null || b.Id < 0 || b.Id >= BuffID.Count) continue;
                ts.SetBuff(b.Id, Math.Max(1, b.Seconds) * 60, true);
            }

            var accountName = ts.Account != null ? ts.Account.Name : ts.Name;
            foreach (var c in g.Commands)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                // 以服务器控制台身份执行；$ACCOUNT$ 替换为账号名
                TShockAPI.Commands.HandleCommand(TSPlayer.Server, "/" + c.Trim().Replace("$ACCOUNT$", accountName));
            }
        }

        private static void GiveCoins(TSPlayer ts, long copper)
        {
            if (copper <= 0) return;
            long plat = copper / 1000000; copper %= 1000000;
            long gold = copper / 10000; copper %= 10000;
            long silver = copper / 100; copper %= 100;
            if (plat > 0) ts.GiveItem(ItemID.PlatinumCoin, (int)Math.Min(plat, 999999));
            if (gold > 0) ts.GiveItem(ItemID.GoldCoin, (int)Math.Min(gold, 999999));
            if (silver > 0) ts.GiveItem(ItemID.SilverCoin, (int)Math.Min(silver, 999999));
            if (copper > 0) ts.GiveItem(ItemID.CopperCoin, (int)Math.Min(copper, 999999));
        }

        // ---------------- 展示辅助 ----------------

        public static string DescribeTrigger(GiftDefinition g)
        {
            var t = g.Trigger;
            switch (t.Type)
            {
                case GiftTriggerType.FirstJoin:
                    return "首次进入服务器";
                case GiftTriggerType.Playtime:
                    return t.Minutes >= 120
                        ? "累计游玩 " + (t.Minutes / 60) + " 小时"
                        : "累计游玩 " + t.Minutes + " 分钟";
                case GiftTriggerType.BossKill:
                {
                    string target = t.NpcIds.Length == 0
                        ? "任意 Boss"
                        : string.Join("/", t.NpcIds.Select(NpcName));
                    return "击败 " + target + (t.Count > 1 ? " x" + t.Count : "");
                }
                case GiftTriggerType.Death:
                    return "累计死亡 " + t.Count + " 次";
                case GiftTriggerType.Event:
                    return DescribeEvent(t.EventKey);
                default:
                    return "";
            }
        }

        public static string NpcName(int netId)
        {
            try
            {
                string name = Lang.GetNPCNameValue(netId);
                return string.IsNullOrEmpty(name) ? ("NPC#" + netId) : name;
            }
            catch
            {
                return "NPC#" + netId;
            }
        }

        public static string ItemName(int itemId)
        {
            try
            {
                string name = Lang.GetItemNameValue(itemId);
                return string.IsNullOrEmpty(name) ? ("物品#" + itemId) : name;
            }
            catch
            {
                return "物品#" + itemId;
            }
        }

        /// <summary>把铜币数额格式化为人类可读形式。</summary>
        public static string FormatMoney(long copper)
        {
            if (copper <= 0) return "";
            var sb = new StringBuilder();
            long plat = copper / 1000000; copper %= 1000000;
            long gold = copper / 10000; copper %= 10000;
            long silver = copper / 100; copper %= 100;
            if (plat > 0) sb.Append(plat + "铂");
            if (gold > 0) sb.Append(gold + "金");
            if (silver > 0) sb.Append(silver + "银");
            if (copper > 0) sb.Append(copper + "铜");
            return sb.ToString();
        }
    }
}
