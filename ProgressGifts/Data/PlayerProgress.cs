using System;
using System.Collections.Generic;

namespace ProgressGifts.Data
{
    /// <summary>单个玩家的进度数据（内存中的活动对象，跨线程访问需加锁）。</summary>
    public sealed class PlayerProgress
    {
        public int AccountId;
        public string AccountName = "";

        /// <summary>累计在线秒数</summary>
        public long TotalSecondsOnline;

        /// <summary>累计死亡次数</summary>
        public int DeathCount;

        /// <summary>是否已被本插件记录过首次进服</summary>
        public bool FirstJoinSeen;

        /// <summary>是否经历过困难模式</summary>
        public bool HardmodeReached;

        public DateTime LastSeenUtc = DateTime.UtcNow;

        /// <summary>netID → 击杀次数（仅记录 Boss 或配置关注的 NPC）</summary>
        public Dictionary<int, int> BossKills = new Dictionary<int, int>();

        /// <summary>自定义事件键 → 数值（大于 0 视为达成）</summary>
        public Dictionary<string, int> EventFlags =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Boss 击杀总次数</summary>
        public int TotalBossKills
        {
            get
            {
                int sum = 0;
                foreach (var v in BossKills.Values) sum += v;
                return sum;
            }
        }
    }
}
