using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace ProgressGifts.Data
{
    /// <summary>
    /// 进度数据库（SQLite）。所有操作内部加锁，可从任意线程调用。
    /// 表结构：
    ///   pg_accounts     玩家进度主表
    ///   pg_boss_kills   Boss 击杀计数
    ///   pg_event_flags  自定义事件标记
    ///   pg_claims       礼包领取记录
    /// </summary>
    public sealed class ProgressDatabase : IDisposable
    {
        private readonly object _sync = new object();
        private readonly SqliteConnection _conn;

        public ProgressDatabase(string path)
        {
            _conn = new SqliteConnection("Data Source=" + path);
            _conn.Open();
        }

        public void Migrate()
        {
            lock (_sync)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS pg_accounts (
    account_id       INTEGER NOT NULL PRIMARY KEY,
    account_name     TEXT    NOT NULL DEFAULT '',
    total_seconds    INTEGER NOT NULL DEFAULT 0,
    death_count      INTEGER NOT NULL DEFAULT 0,
    first_join_seen  INTEGER NOT NULL DEFAULT 0,
    hardmode_reached INTEGER NOT NULL DEFAULT 0,
    last_seen_utc    TEXT    NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS pg_boss_kills (
    account_id INTEGER NOT NULL,
    npc_id     INTEGER NOT NULL,
    kills      INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (account_id, npc_id)
);
CREATE TABLE IF NOT EXISTS pg_event_flags (
    account_id INTEGER NOT NULL,
    flag_key   TEXT    NOT NULL,
    flag_value INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (account_id, flag_key)
);
CREATE TABLE IF NOT EXISTS pg_claims (
    account_id  INTEGER NOT NULL,
    gift_id     TEXT    NOT NULL,
    granted_utc TEXT    NOT NULL,
    source      TEXT    NOT NULL DEFAULT '',
    PRIMARY KEY (account_id, gift_id)
);";
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>读取玩家进度；不存在则创建空记录。</summary>
        public PlayerProgress LoadOrCreate(int accountId, string accountName)
        {
            lock (_sync)
            {
                var p = ReadAccount(accountId);
                if (p != null)
                {
                    // 名字可能变更，顺带更新
                    if (p.AccountName != accountName)
                    {
                        p.AccountName = accountName;
                    }
                    return p;
                }

                p = new PlayerProgress { AccountId = accountId, AccountName = accountName };
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO pg_accounts
    (account_id, account_name, total_seconds, death_count, first_join_seen, hardmode_reached, last_seen_utc)
VALUES ($id, $name, 0, 0, 0, 0, $seen);";
                    cmd.Parameters.AddWithValue("$id", accountId);
                    cmd.Parameters.AddWithValue("$name", accountName);
                    cmd.Parameters.AddWithValue("$seen", p.LastSeenUtc.ToString("O"));
                    cmd.ExecuteNonQuery();
                }
                return p;
            }
        }

        private PlayerProgress ReadAccount(int accountId)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT account_name, total_seconds, death_count, first_join_seen, hardmode_reached, last_seen_utc
FROM pg_accounts WHERE account_id = $id;";
                cmd.Parameters.AddWithValue("$id", accountId);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    var p = new PlayerProgress
                    {
                        AccountId = accountId,
                        AccountName = r.GetString(0),
                        TotalSecondsOnline = r.GetInt64(1),
                        DeathCount = r.GetInt32(2),
                        FirstJoinSeen = r.GetInt64(3) != 0,
                        HardmodeReached = r.GetInt64(4) != 0,
                    };
                    DateTime.TryParse(r.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind, out var seen);
                    p.LastSeenUtc = seen == default ? DateTime.UtcNow : seen.ToUniversalTime();
                    return p;
                }
            }
        }

        /// <summary>保存玩家进度（整行覆盖写入）。</summary>
        public void Save(PlayerProgress p)
        {
            lock (_sync)
            {
                p.LastSeenUtc = DateTime.UtcNow;
                using (var tx = _conn.BeginTransaction())
                {
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"
INSERT INTO pg_accounts
    (account_id, account_name, total_seconds, death_count, first_join_seen, hardmode_reached, last_seen_utc)
VALUES ($id, $name, $sec, $death, $first, $hard, $seen)
ON CONFLICT(account_id) DO UPDATE SET
    account_name = excluded.account_name,
    total_seconds = excluded.total_seconds,
    death_count = excluded.death_count,
    first_join_seen = excluded.first_join_seen,
    hardmode_reached = excluded.hardmode_reached,
    last_seen_utc = excluded.last_seen_utc;";
                        cmd.Parameters.AddWithValue("$id", p.AccountId);
                        cmd.Parameters.AddWithValue("$name", p.AccountName ?? "");
                        cmd.Parameters.AddWithValue("$sec", p.TotalSecondsOnline);
                        cmd.Parameters.AddWithValue("$death", p.DeathCount);
                        cmd.Parameters.AddWithValue("$first", p.FirstJoinSeen ? 1 : 0);
                        cmd.Parameters.AddWithValue("$hard", p.HardmodeReached ? 1 : 0);
                        cmd.Parameters.AddWithValue("$seen", p.LastSeenUtc.ToString("O"));
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM pg_boss_kills WHERE account_id = $id;";
                        cmd.Parameters.AddWithValue("$id", p.AccountId);
                        cmd.ExecuteNonQuery();
                    }
                    foreach (var kv in p.BossKills)
                    {
                        using (var cmd = _conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText =
                                "INSERT OR REPLACE INTO pg_boss_kills (account_id, npc_id, kills) VALUES ($id, $npc, $kills);";
                            cmd.Parameters.AddWithValue("$id", p.AccountId);
                            cmd.Parameters.AddWithValue("$npc", kv.Key);
                            cmd.Parameters.AddWithValue("$kills", kv.Value);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = "DELETE FROM pg_event_flags WHERE account_id = $id;";
                        cmd.Parameters.AddWithValue("$id", p.AccountId);
                        cmd.ExecuteNonQuery();
                    }
                    foreach (var kv in p.EventFlags)
                    {
                        using (var cmd = _conn.CreateCommand())
                        {
                            cmd.Transaction = tx;
                            cmd.CommandText =
                                "INSERT OR REPLACE INTO pg_event_flags (account_id, flag_key, flag_value) VALUES ($id, $k, $v);";
                            cmd.Parameters.AddWithValue("$id", p.AccountId);
                            cmd.Parameters.AddWithValue("$k", kv.Key);
                            cmd.Parameters.AddWithValue("$v", kv.Value);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    tx.Commit();
                }
            }
        }

        /// <summary>读取玩家进度与附属数据。</summary>
        public PlayerProgress LoadFull(int accountId)
        {
            lock (_sync)
            {
                var p = ReadAccount(accountId);
                if (p == null) return null;

                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT npc_id, kills FROM pg_boss_kills WHERE account_id = $id;";
                    cmd.Parameters.AddWithValue("$id", accountId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            p.BossKills[r.GetInt32(0)] = r.GetInt32(1);
                        }
                    }
                }

                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT flag_key, flag_value FROM pg_event_flags WHERE account_id = $id;";
                    cmd.Parameters.AddWithValue("$id", accountId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            p.EventFlags[r.GetString(0)] = r.GetInt32(1);
                        }
                    }
                }
                return p;
            }
        }

        /// <summary>设置自定义事件标记（直接写库，用于离线玩家）。</summary>
        public void SetEventFlag(int accountId, string key, int value)
        {
            lock (_sync)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT INTO pg_event_flags (account_id, flag_key, flag_value) VALUES ($id, $k, $v)
ON CONFLICT(account_id, flag_key) DO UPDATE SET flag_value = excluded.flag_value;";
                    cmd.Parameters.AddWithValue("$id", accountId);
                    cmd.Parameters.AddWithValue("$k", key);
                    cmd.Parameters.AddWithValue("$v", value);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>获取玩家已领取的礼包 ID 集合。</summary>
        public HashSet<string> GetClaimIds(int accountId)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (_sync)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT gift_id FROM pg_claims WHERE account_id = $id;";
                    cmd.Parameters.AddWithValue("$id", accountId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read()) set.Add(r.GetString(0));
                    }
                }
            }
            return set;
        }

        /// <summary>写入领取记录（幂等）。</summary>
        public void SetClaim(int accountId, string giftId, string source)
        {
            lock (_sync)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT OR IGNORE INTO pg_claims (account_id, gift_id, granted_utc, source)
VALUES ($id, $gift, $utc, $src);";
                    cmd.Parameters.AddWithValue("$id", accountId);
                    cmd.Parameters.AddWithValue("$gift", giftId);
                    cmd.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
                    cmd.Parameters.AddWithValue("$src", source ?? "");
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>删除一条领取记录，返回是否确实删除了。</summary>
        public bool RemoveClaim(int accountId, string giftId)
        {
            lock (_sync)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM pg_claims WHERE account_id = $id AND gift_id = $gift;";
                    cmd.Parameters.AddWithValue("$id", accountId);
                    cmd.Parameters.AddWithValue("$gift", giftId);
                    return cmd.ExecuteNonQuery() > 0;
                }
            }
        }

        /// <summary>清空玩家全部领取记录，返回删除条数。</summary>
        public int RemoveAllClaims(int accountId)
        {
            lock (_sync)
            {
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM pg_claims WHERE account_id = $id;";
                    cmd.Parameters.AddWithValue("$id", accountId);
                    return cmd.ExecuteNonQuery();
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _conn.Dispose();
            }
        }
    }
}
