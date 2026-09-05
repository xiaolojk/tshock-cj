using System;
using System.Collections.Generic;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;

namespace ProgressGifts.Services
{
    /// <summary>
    /// 心跳服务：挂接 GamePostUpdate（主线程），驱动 Boss 轮询与每秒的玩家会话逻辑；
    /// 同时订阅 KillMe 数据包事件实现精确的死亡计数。
    /// </summary>
    public sealed class HeartbeatService
    {
        private readonly ProgressGiftPlugin _plugin;
        private readonly BossWatcher _bossWatcher;
        private int _tick;
        private readonly HashSet<int> _onlineAccountIds = new HashSet<int>();

        public HeartbeatService(ProgressGiftPlugin plugin)
        {
            _plugin = plugin;
            _bossWatcher = new BossWatcher(OnBossKilled);
        }

        public void Start()
        {
            RefreshTrackedNpcIds();
            ServerApi.Hooks.GamePostUpdate.Register(_plugin, OnGamePostUpdate);
            GetDataHandlers.KillMe += OnKillMe;
        }

        public void Stop()
        {
            ServerApi.Hooks.GamePostUpdate.Deregister(_plugin, OnGamePostUpdate);
            GetDataHandlers.KillMe -= OnKillMe;
            _bossWatcher.Reset();
        }

        /// <summary>配置加载/重载后调用：更新 Boss 跟踪列表。</summary>
        public void RefreshTrackedNpcIds()
        {
            var ids = new HashSet<int>();
            foreach (var g in _plugin.Config.Gifts)
            {
                if (g?.Trigger?.NpcIds == null) continue;
                foreach (var id in g.Trigger.NpcIds) ids.Add(id);
            }
            _bossWatcher.SetTrackedIds(ids);
        }

        // ---------------- 钩子 ----------------

        private void OnGamePostUpdate(EventArgs args)
        {
            _tick++;
            if (_tick % 20 == 0) // ~每 1/3 秒
            {
                try
                {
                    _bossWatcher.Poll();
                }
                catch (Exception ex)
                {
                    TShock.Log.Error("[进度礼包] BossWatcher 异常：" + ex);
                }
            }

            if (_tick % 60 == 0) // 每秒
            {
                try
                {
                    SecondTick();
                }
                catch (Exception ex)
                {
                    TShock.Log.Error("[进度礼包] 心跳异常：" + ex);
                }
            }
        }

        private void OnKillMe(object sender, GetDataHandlers.KillMeEventArgs args)
        {
            try
            {
                TSPlayer ts = args.Player;
                if (ts == null && args.PlayerId >= 0 && args.PlayerId < TShock.Players.Length)
                {
                    ts = TShock.Players[args.PlayerId];
                }
                if (ts == null || !ts.Active || !ts.IsLoggedIn || ts.Account == null) return;
                _plugin.Gifts.RegisterDeath(ts);
            }
            catch (Exception ex)
            {
                TShock.Log.Error("[进度礼包] 死亡统计异常：" + ex);
            }
        }

        private void OnBossKilled(int netId)
        {
            _plugin.Gifts.RegisterBossKill(netId);
        }

        // ---------------- 每秒逻辑 ----------------

        private void SecondTick()
        {
            _onlineAccountIds.Clear();

            var players = TShock.Players;
            for (int i = 0; i < players.Length; i++)
            {
                var ts = players[i];
                if (ts == null || !ts.Active) continue;
                if (!ts.IsLoggedIn || ts.Account == null) continue;

                _onlineAccountIds.Add(ts.Account.ID);

                try
                {
                    _plugin.Gifts.TickSession(ts);
                }
                catch (Exception ex)
                {
                    TShock.Log.Error("[进度礼包] 玩家会话异常（" + ts.Name + "）：" + ex);
                }
            }

            _plugin.Gifts.FlushPassiveSaves(_onlineAccountIds);
        }
    }
}
