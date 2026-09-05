using System;
using System.Collections.Generic;
using Terraria;

namespace ProgressGifts.Services
{
    /// <summary>
    /// Boss 击杀监视器：以固定间隔轮询 Main.npc，
    /// 当被跟踪的 Boss 从世界中消失且确实被交战过（掉过血）、
    /// 且当时至少有一名玩家存活时，判定为“被击败”。
    /// 只依赖 Terraria 原生状态，不依赖特定版本的封包钩子。
    /// </summary>
    public sealed class BossWatcher
    {
        private sealed class TrackedBoss
        {
            public int NetId;
            public float MinLifeRatio = 1f;
        }

        private readonly Action<int> _onBossKilled;
        private readonly Dictionary<int, TrackedBoss> _active = new Dictionary<int, TrackedBoss>();
        private HashSet<int> _extraIds = new HashSet<int>();

        public BossWatcher(Action<int> onBossKilled)
        {
            _onBossKilled = onBossKilled ?? throw new ArgumentNullException(nameof(onBossKilled));
        }

        /// <summary>除 boss 标记外需要额外跟踪的 NPC netID 集合（来自各礼包配置的并集）。</summary>
        public void SetTrackedIds(HashSet<int> ids)
        {
            _extraIds = ids ?? new HashSet<int>();
        }

        /// <summary>每约 1/3 秒在主线程调用一次。</summary>
        public void Poll()
        {
            List<TrackedBoss> finished = null;

            for (int i = 0; i < Main.maxNPCs; i++)
            {
                var npc = Main.npc[i];
                if (npc == null) continue;

                bool trackedNow = npc.active && (npc.boss || _extraIds.Contains(npc.netID));

                if (trackedNow)
                {
                    if (!_active.TryGetValue(i, out var t) || t.NetId != npc.netID)
                    {
                        if (t != null)
                        {
                            finished ??= new List<TrackedBoss>();
                            finished.Add(t);
                        }
                        t = new TrackedBoss { NetId = npc.netID };
                        _active[i] = t;
                    }

                    if (npc.lifeMax > 0)
                    {
                        float ratio = (float)npc.life / npc.lifeMax;
                        if (ratio < t.MinLifeRatio) t.MinLifeRatio = ratio;
                    }

                    if (t.MinLifeRatio <= 0f)
                    {
                        _active.Remove(i);
                        finished ??= new List<TrackedBoss>();
                        finished.Add(t);
                    }
                }
                else if (_active.TryGetValue(i, out var old))
                {
                    _active.Remove(i);
                    finished ??= new List<TrackedBoss>();
                    finished.Add(old);
                }
            }

            if (finished == null || finished.Count == 0) return;

            // 全员阵亡导致的 Boss 撤退不算击杀
            bool anyPlayerAlive = false;
            for (int p = 0; p < Main.maxPlayers; p++)
            {
                var plr = Main.player[p];
                if (plr != null && plr.active && !plr.dead)
                {
                    anyPlayerAlive = true;
                    break;
                }
            }

            // 同一次轮询内多个分段（如克苏鲁之脑/世界吞噬者）只记一次
            var killedIds = new HashSet<int>();
            foreach (var t in finished)
            {
                if (t.MinLifeRatio < 0.995f && anyPlayerAlive)
                {
                    killedIds.Add(t.NetId);
                }
            }

            foreach (var id in killedIds)
            {
                _onBossKilled(id);
            }
        }

        /// <summary>服务器换图/重置时清空跟踪状态。</summary>
        public void Reset()
        {
            _active.Clear();
        }
    }
}
