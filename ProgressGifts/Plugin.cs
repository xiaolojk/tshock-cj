using System;
using System.IO;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using ProgressGifts.Commands;
using ProgressGifts.Data;
using ProgressGifts.Services;

namespace ProgressGifts
{
    /// <summary>
    /// 进度礼包插件（TShock 6.1 / Terraria 1.4.5.6 / .NET 9）
    /// 按游玩时长、Boss 讨伐、死亡次数、事件等进度向玩家发放礼包。
    /// </summary>
    [ApiVersion(2, 1)]
    public class ProgressGiftPlugin : TerrariaPlugin
    {
        public override string Name => "ProgressGifts";
        public override string Author => "ProgressGifts Contributors";
        public override string Description => "进度礼包：按游玩时长/Boss 讨伐/死亡/事件等进度发放奖励";
        public override Version Version => new Version(1, 0, 0);

        internal static ProgressGiftPlugin Instance { get; private set; }

        internal Configuration Config { get; private set; }
        internal ProgressDatabase Database { get; private set; }
        internal GiftService Gifts { get; private set; }
        internal HeartbeatService Heartbeat { get; private set; }

        internal string DataDirectory { get; private set; }

        public ProgressGiftPlugin(Main game) : base(game)
        {
            // 在 TShock 本体之后加载
            Order = 100;
        }

        public override void Initialize()
        {
            Instance = this;

            DataDirectory = Path.Combine(TShock.SavePath, "ProgressGifts");
            Directory.CreateDirectory(DataDirectory);

            Config = Configuration.Load(Path.Combine(DataDirectory, "config.json"));

            Database = new ProgressDatabase(Path.Combine(DataDirectory, "progress.sqlite3"));
            Database.Migrate();

            Gifts = new GiftService(this);
            Heartbeat = new HeartbeatService(this);
            Heartbeat.Start();

            GiftCommands.Register(this);

            TShock.Log.ConsoleInfo("[进度礼包] v{0} 已加载：{1} 个礼包，数据目录 {2}",
                Version, Config.Gifts.Count, DataDirectory);
        }

        /// <summary>重载配置（/gift reload）。</summary>
        internal void ReloadConfig()
        {
            Config = Configuration.Load(Path.Combine(DataDirectory, "config.json"));
            Heartbeat.RefreshTrackedNpcIds();
            TShock.Log.ConsoleInfo("[进度礼包] 配置已重载：{0} 个礼包。", Config.Gifts.Count);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Heartbeat?.Stop();
                Gifts?.Shutdown();
                Database?.Dispose();
                Instance = null;
            }
            base.Dispose(disposing);
        }
    }
}
