using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ProgressGifts.Services;
using TShockAPI;

namespace ProgressGifts.Commands
{
    /// <summary>/gift（/礼包）命令：查看、领取、管理。</summary>
    public static class GiftCommands
    {
        private const string AdminPerm = "progressgift.admin";

        private static ProgressGiftPlugin _plugin;

        public static void Register(ProgressGiftPlugin plugin)
        {
            _plugin = plugin;
            // 基础命令不设权限门槛（默认组开箱即用）；管理子命令由 RequireAdmin 内部校验 progressgift.admin
            var cmd = new Command(Execute, "gift", "礼包")
            {
                HelpText = "进度礼包：/gift [页码] 查看，/gift claim <编号> 领取，/gift info <编号> 详情",
                AllowServer = true,
            };
            TShockAPI.Commands.ChatCommands.Add(cmd);
        }

        private static void Execute(CommandArgs args)
        {
            var ts = args.Player;
            var p = args.Parameters;

            // /gift 或 /gift <页码>
            if (p.Count == 0 || (p.Count == 1 && int.TryParse(p[0], out _)))
            {
                ShowList(ts, p.Count == 0 ? 1 : int.Parse(p[0]));
                return;
            }

            string sub = p[0].ToLowerInvariant();
            switch (sub)
            {
                case "claim":
                case "领取":
                    DoClaim(ts, p);
                    break;
                case "info":
                case "详情":
                    DoInfo(ts, p);
                    break;
                case "stats":
                case "进度":
                    DoStats(ts, p);
                    break;
                case "reload":
                case "重载":
                    RequireAdmin(ts, DoReload);
                    break;
                case "grant":
                case "发放":
                    RequireAdmin(ts, () => DoGrant(ts, p));
                    break;
                case "reset":
                case "重置":
                    RequireAdmin(ts, () => DoReset(ts, p));
                    break;
                case "setflag":
                case "标记":
                    RequireAdmin(ts, () => DoSetFlag(ts, p));
                    break;
                default:
                    ts.SendErrorMessage("未知的子命令。用法：/gift [页码] | claim <编号> | info <编号> | stats [玩家]");
                    break;
            }
        }

        private static void RequireAdmin(TSPlayer ts, Action action)
        {
            if (!ts.HasPermission(AdminPerm))
            {
                ts.SendErrorMessage("你没有权限执行该操作。");
                return;
            }
            action();
        }

        private static bool IsRealPlayer(TSPlayer ts)
        {
            return !(ts is TSServerPlayer) && ts.Account != null && ts.IsLoggedIn;
        }

        // ---------------- 列表 ----------------

        private static void ShowList(TSPlayer ts, int page)
        {
            var cfg = _plugin.Config;
            var visible = cfg.Gifts.Where(g => g != null && !g.Hidden && !string.IsNullOrEmpty(g.Id)).ToList();
            if (visible.Count == 0)
            {
                ts.SendInfoMessage("[进度礼包] 当前没有配置任何礼包。");
                return;
            }

            // 控制台：仅列出定义
            if (!IsRealPlayer(ts))
            {
                ts.SendInfoMessage("[进度礼包] 共 {0} 个礼包（控制台视角，不显示领取状态）：", visible.Count);
                for (int i = 0; i < visible.Count; i++)
                {
                    ts.SendInfoMessage("  [{0}] {1} —— {2}", i + 1, visible[i].Name, GiftService.DescribeTrigger(visible[i]));
                }
                return;
            }

            var session = _plugin.Gifts.GetSession(ts);
            int pageSize = Math.Max(1, cfg.Settings.ListPageSize);
            int pageCount = (visible.Count + pageSize - 1) / pageSize;
            page = Math.Max(1, Math.Min(page, pageCount));

            ts.SendInfoMessage("===== 进度礼包（第 {0}/{1} 页）=====", page, pageCount);
            int start = (page - 1) * pageSize;
            int end = Math.Min(start + pageSize, visible.Count);
            for (int i = start; i < end; i++)
            {
                var g = visible[i];
                var e = _plugin.Gifts.Evaluate(g, session);
                string status;
                switch (e.State)
                {
                    case GiftService.GiftState.Claimable: status = "[可领取]"; break;
                    case GiftService.GiftState.Claimed: status = "[已领取]"; break;
                    default: status = "[" + e.ProgressText + "]"; break;
                }
                ts.SendMessage(
                    string.Format("[{0}] {1} —— {2} {3}", i + 1, g.Name, GiftService.DescribeTrigger(g), status),
                    e.State == GiftService.GiftState.Claimable
                        ? new Microsoft.Xna.Framework.Color(120, 255, 120)
                        : new Microsoft.Xna.Framework.Color(200, 200, 200));
            }
            ts.SendInfoMessage("输入 /gift claim <编号> 领取，/gift info <编号> 查看详情，/gift <页码> 翻页。");
        }

        // ---------------- 领取 ----------------

        private static void DoClaim(TSPlayer ts, List<string> p)
        {
            if (!IsRealPlayer(ts))
            {
                ts.SendErrorMessage("该命令只能由游戏内玩家执行。");
                return;
            }
            if (p.Count < 2 || !int.TryParse(p[1], out int n) || n < 1)
            {
                ts.SendErrorMessage("用法：/gift claim <编号>（编号见 /gift 列表）。");
                return;
            }

            var visible = VisibleGifts();
            if (n > visible.Count)
            {
                ts.SendErrorMessage("编号超出范围，请输入 /gift 查看列表。");
                return;
            }

            var g = visible[n - 1];
            var session = _plugin.Gifts.GetSession(ts);
            if (!_plugin.Gifts.TryClaim(ts, session, g, "manual"))
            {
                ts.SendErrorMessage("「{0}」尚未达成条件或已领取过。", g.Name);
            }
        }

        private static void DoInfo(TSPlayer ts, List<string> p)
        {
            if (p.Count < 2 || !int.TryParse(p[1], out int n) || n < 1)
            {
                ts.SendErrorMessage("用法：/gift info <编号>。");
                return;
            }
            var visible = VisibleGifts();
            if (n > visible.Count)
            {
                ts.SendErrorMessage("编号超出范围。");
                return;
            }

            var g = visible[n - 1];
            var sb = new StringBuilder();
            sb.AppendFormat("[{0}] {1} —— {2}", n, g.Name, GiftService.DescribeTrigger(g)).AppendLine();
            if (!string.IsNullOrEmpty(g.Description)) sb.AppendLine("  " + g.Description);

            if (g.Items.Count > 0)
            {
                var parts = g.Items
                    .Where(i => i != null && i.Id > 0)
                    .Select(i => GiftService.ItemName(i.Id) + (i.Stack > 1 ? " x" + i.Stack : ""));
                sb.AppendLine("  物品：" + string.Join("、", parts));
            }
            var money = GiftService.FormatMoney(g.Money);
            if (money.Length > 0) sb.AppendLine("  金币：" + money);
            if (g.Buffs.Count > 0) sb.AppendLine("  Buff：" + g.Buffs.Count + " 个");
            if (g.AutoGrant) sb.AppendLine("  （达成后自动发放）");

            ts.SendInfoMessage("{0}", sb.ToString().TrimEnd());
        }

        // ---------------- 进度 ----------------

        private static void DoStats(TSPlayer ts, List<string> p)
        {
            TSPlayer target = ts;
            bool isAdmin = ts.HasPermission(AdminPerm);

            if (p.Count >= 2)
            {
                if (!isAdmin)
                {
                    ts.SendErrorMessage("你没有权限查看他人进度。");
                    return;
                }
                var found = FindPlayers(p[1]);
                if (found.Count == 0)
                {
                    ts.SendErrorMessage("未找到在线玩家：{0}", p[1]);
                    return;
                }
                if (found.Count > 1)
                {
                    ts.SendErrorMessage("匹配到多名玩家：{0}", string.Join(", ", found.Select(f => f.Name)));
                    return;
                }
                target = found[0];
            }

            if (!IsRealPlayer(target))
            {
                ts.SendErrorMessage("目标玩家未登录。");
                return;
            }

            var s = _plugin.Gifts.GetSession(target);
            string text;
            lock (s.Sync)
            {
                var pr = s.Progress;
                var bossParts = pr.BossKills
                    .Where(kv => kv.Value > 0)
                    .Select(kv => GiftService.NpcName(kv.Key) + "x" + kv.Value);
                text = string.Format(
                    "玩家 {0} 进度：游玩 {1:0.#} 小时 | 死亡 {2} 次 | Boss 击杀 {3} 次{4} | 困难模式：{5}",
                    pr.AccountName,
                    pr.TotalSecondsOnline / 3600.0,
                    pr.DeathCount,
                    pr.TotalBossKills,
                    pr.BossKills.Count > 0 ? "（" + string.Join(", ", bossParts) + "）" : "",
                    pr.HardmodeReached ? "是" : "否");
            }
            ts.SendInfoMessage("[进度礼包] {0}", text);
        }

        // ---------------- 管理 ----------------

        private static void DoReload()
        {
            _plugin.ReloadConfig();
        }

        private static void DoGrant(TSPlayer ts, List<string> p)
        {
            if (p.Count < 3)
            {
                ts.SendErrorMessage("用法：/gift grant <玩家> <编号>（无视条件直接发放）");
                return;
            }
            var found = FindPlayers(p[1]);
            if (found.Count == 0)
            {
                ts.SendErrorMessage("未找到在线玩家：{0}", p[1]);
                return;
            }
            if (found.Count > 1)
            {
                ts.SendErrorMessage("匹配到多名玩家：{0}", string.Join(", ", found.Select(f => f.Name)));
                return;
            }
            if (!int.TryParse(p[2], out int n) || n < 1 || n > VisibleGifts().Count)
            {
                ts.SendErrorMessage("礼包编号无效。");
                return;
            }

            var target = found[0];
            if (!IsRealPlayer(target))
            {
                ts.SendErrorMessage("目标玩家未登录。");
                return;
            }

            var g = VisibleGifts()[n - 1];
            var session = _plugin.Gifts.GetSession(target);
            if (_plugin.Gifts.TryClaim(target, session, g, "admin"))
            {
                if (ts != target) ts.SendSuccessMessage("已向 {0} 发放「{1}」。", target.Name, g.Name);
            }
            else
            {
                ts.SendErrorMessage("{0} 已经领取过「{1}」。", target.Name, g.Name);
            }
        }

        private static void DoReset(TSPlayer ts, List<string> p)
        {
            if (p.Count < 2)
            {
                ts.SendErrorMessage("用法：/gift reset <玩家> [编号|all]（玩家可离线）");
                return;
            }

            // 在线玩家优先，其次按账号名查找（支持离线）
            int accountId;
            string displayName;
            var found = FindPlayers(p[1]);
            if (found.Count == 1 && found[0].Account != null)
            {
                accountId = found[0].Account.ID;
                displayName = found[0].Account.Name;
            }
            else
            {
                var acc = TShock.UserAccounts.GetUserAccountByName(p[1]);
                if (acc == null)
                {
                    ts.SendErrorMessage("未找到账号：{0}", p[1]);
                    return;
                }
                accountId = acc.ID;
                displayName = acc.Name;
            }

            if (p.Count >= 3 && !p[2].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(p[2], out int n) || n < 1 || n > VisibleGifts().Count)
                {
                    ts.SendErrorMessage("礼包编号无效。");
                    return;
                }
                var g = VisibleGifts()[n - 1];
                _plugin.Gifts.AdminReset(accountId, g.Id);
                ts.SendSuccessMessage("已重置 {0} 的「{1}」领取记录。", displayName, g.Name);
            }
            else
            {
                _plugin.Gifts.AdminReset(accountId, null);
                ts.SendSuccessMessage("已清空 {0} 的全部领取记录。", displayName);
            }
        }

        private static void DoSetFlag(TSPlayer ts, List<string> p)
        {
            if (p.Count < 3)
            {
                ts.SendErrorMessage("用法：/gift setflag <玩家> <事件键> [值=1]（玩家可离线）");
                return;
            }

            int accountId;
            TSPlayer online = null;
            var found = FindPlayers(p[1]);
            if (found.Count == 1 && found[0].Account != null && found[0].IsLoggedIn)
            {
                online = found[0];
                accountId = online.Account.ID;
            }
            else
            {
                var acc = TShock.UserAccounts.GetUserAccountByName(p[1]);
                if (acc == null)
                {
                    ts.SendErrorMessage("未找到账号：{0}", p[1]);
                    return;
                }
                accountId = acc.ID;
            }

            string key = p[2];
            int value = 1;
            if (p.Count >= 4 && (!int.TryParse(p[3], out value)))
            {
                ts.SendErrorMessage("值必须是整数。");
                return;
            }

            _plugin.Gifts.SetEventFlag(accountId, key, value, online);
            ts.SendSuccessMessage("已将 {0} 的事件标记 {1} 设为 {2}。", p[1], key, value);
        }

        // ---------------- 辅助 ----------------

        private static List<GiftDefinition> VisibleGifts()
        {
            return _plugin.Config.Gifts
                .Where(g => g != null && !g.Hidden && !string.IsNullOrEmpty(g.Id))
                .ToList();
        }

        private static List<TSPlayer> FindPlayers(string name)
        {
            var lower = name.ToLowerInvariant();
            var exact = new List<TSPlayer>();
            var prefix = new List<TSPlayer>();
            foreach (var p in TShock.Players)
            {
                if (p == null || !p.Active) continue;
                var n = p.TPlayer.name;
                if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) exact.Add(p);
                else if (name.Length >= 2 && n.ToLowerInvariant().StartsWith(lower)) prefix.Add(p);
            }
            return exact.Count > 0 ? exact : prefix;
        }
    }
}
