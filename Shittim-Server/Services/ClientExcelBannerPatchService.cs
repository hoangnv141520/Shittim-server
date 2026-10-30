using BlueArchiveAPI.Configuration;
using BlueArchiveAPI.Services;
using Microsoft.Data.Sqlite;
using Schale.Crypto;
using Schale.FlatData;

namespace Shittim_Server.Services
{
    // The recruitment list comes from the client's ExcelDB.db. Pickup tab artwork
    // normally comes from Management_BannerList; GachaBannerPath is its asset fallback.
    // Only reuse art backed by the same pickup characters or explicit banner link.
    // Numeric ID prefixes do not identify artwork: normal and pickup rows share them.
    // Read donors from the server's resource database so earlier client patches cannot
    // become the source of another incorrect replacement.
    public class ClientExcelBannerPatchService : IHostedService
    {
        private const string SchemaTable = "ShopRecruitDBSchema";

        private readonly ILogger<ClientExcelBannerPatchService> logger;

        public ClientExcelBannerPatchService(ILogger<ClientExcelBannerPatchService> logger)
        {
            this.logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!Config.Instance.ServerConfiguration.AutoPatchClientBanners)
                    return Task.CompletedTask;

                var dbPath = GetExcelDbPath();
                if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
                {
                    logger.LogWarning("Client banner auto-patch enabled but client ExcelDB.db was not found ({Path})", dbPath);
                    return Task.CompletedTask;
                }

                PatchBanners(dbPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to patch client recruitment banners");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private void PatchBanners(string dbPath)
        {
            SqliteProvider.EnsureInitialized();
            lock (ExcelTableService.loadLock)
                PatchBannersLocked(dbPath);
        }

        private void PatchBannersLocked(string dbPath)
        {
            var sourcePath = Path.Combine(ResourceService.DumpedDir, "ExcelDB.db");
            if (!File.Exists(sourcePath) || Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(dbPath), StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Client recruitment banners: independent resource database unavailable; skipping");
                return;
            }
            // ExcelDB flatbuffer strings are stored plaintext (the SQLCipher layer is the only encryption); read and write them as-is so non-modified fields round-trip byte-equal.
            TableEncryptionService.UseEncryption = false;

            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWrite
            }.ToString());
            conn.Open();
            ApplyKey(conn);

            // ShopRecruitExcel has drifted - the client inserted two fields mid-table since the models were generated - so a straight UnPack walks a later field's offset into the middle of another one and throws.
            var slots = ClientExcelSchema.RowSlots(typeof(ShopRecruitExcelT), "ShopRecruitExcel");
            var artSlot = slots[Array.FindIndex(typeof(ShopRecruitExcelT).GetProperties(), p => p.Name == nameof(ShopRecruitExcelT.GachaBannerPath))];

            using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            source.Open();
            ApplyKey(source);
            var originals = ReadRows(source, slots).Select(r => r.Recruit).ToList();
            var byId = originals.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
            var rows = ReadRows(conn, slots);
            var now = DateTime.Now;
            var changes = new List<(BannerRow Row, string Art)>();
            foreach (var row in rows.Where(r => !IsExpired(r.Recruit.SalePeriodTo, now)))
            {
                if (!byId.TryGetValue(row.Recruit.Id, out var original)) continue;
                var replacement = ReplacementArt(row.Recruit, original, originals);
                if (replacement != null) changes.Add((row, replacement));
                else if (string.IsNullOrEmpty(row.Recruit.GachaBannerPath) && original.InfoCharacterId?.Count > 0)
                    logger.LogWarning("Banner {Id}: no matching pickup artwork in resource data; leaving original path", original.Id);
            }

            if (changes.Count == 0)
            {
                logger.LogInformation("Client recruitment banners: no verified art changes to apply");
                return;
            }

            var backupPath = dbPath + $".shittim-banner-backup-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString()))
            {
                backup.Open();
                ApplyKey(backup);
                conn.BackupDatabase(backup);
            }
            logger.LogInformation("Client recruitment banner backup: {Path}", backupPath);

            var patched = 0;
            using (var tx = conn.BeginTransaction())
            {
                foreach (var (target, art) in changes)
                {
                    var newBytes = RealignedRowReader.WithString(target.Bytes, artSlot, art);
                    if (newBytes == null) continue;
                    using var upd = conn.CreateCommand();
                    upd.Transaction = tx;
                    upd.CommandText = $"UPDATE [{SchemaTable}] SET Bytes = @b WHERE rowid = @r";
                    upd.Parameters.Add("@b", SqliteType.Blob).Value = newBytes;
                    upd.Parameters.Add("@r", SqliteType.Integer).Value = target.RowId;
                    upd.ExecuteNonQuery();
                    patched++;
                    logger.LogInformation("Banner {Id}: {OldArt} -> {Art}", target.Recruit.Id, target.Recruit.GachaBannerPath, art);
                }
                tx.Commit();
            }
            logger.LogInformation("Updated {Count} client recruitment banner art path(s)", patched);
        }

        private static List<BannerRow> ReadRows(SqliteConnection conn, int[] slots)
        {
            var rows = new List<BannerRow>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT rowid, Bytes FROM [{SchemaTable}]";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var rowid = reader.GetInt64(0);
                    var bytes = (byte[])reader[1];
                    var rec = (ShopRecruitExcelT)RealignedRowReader.Read(bytes, typeof(ShopRecruitExcelT), slots);
                    rows.Add(new BannerRow(rowid, rec, bytes));
                }
            }

            return rows;
        }

        internal static string? ReplacementArt(ShopRecruitExcelT current, ShopRecruitExcelT original, IReadOnlyCollection<ShopRecruitExcelT> originals)
        {
            // Migrate the old patch only when a pickup's shipped path was empty and
            // the client now contains artwork belonging to a normal recruitment.
            var wrongNormalArt = string.IsNullOrEmpty(original.GachaBannerPath)
                && original.InfoCharacterId?.Count > 0
                && !string.IsNullOrEmpty(current.GachaBannerPath)
                && originals.Any(r => r.CategoryType == ShopCategoryType.NormalGacha
                    && r.GachaBannerPath == current.GachaBannerPath);
            if (!string.IsNullOrEmpty(current.GachaBannerPath) && !wrongNormalArt) return null;

            var art = PickRelatedArt(original, originals);
            if (art == null && wrongNormalArt) art = original.GachaBannerPath ?? string.Empty;
            return art == (current.GachaBannerPath ?? string.Empty) ? null : art;
        }

        internal static string? PickRelatedArt(ShopRecruitExcelT target, IReadOnlyCollection<ShopRecruitExcelT> originals)
        {
            if (!string.IsNullOrEmpty(target.GachaBannerPath)) return target.GachaBannerPath;
            var characters = (target.InfoCharacterId ?? []).ToHashSet();
            var matches = originals.Where(r => !string.IsNullOrEmpty(r.GachaBannerPath)
                && r.CategoryType == target.CategoryType
                && r.IsSelectRecruit == target.IsSelectRecruit
                && r.IsNewbie == target.IsNewbie
                && (characters.Count > 0
                    ? characters.SetEquals(r.InfoCharacterId ?? [])
                    : (r.InfoCharacterId?.Count ?? 0) == 0 && target.LinkedRobbyBannerId > 0
                        && r.LinkedRobbyBannerId == target.LinkedRobbyBannerId))
                .Select(r => r.GachaBannerPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            // Conflicting images are ambiguous; do not select one by row order.
            return matches.Count == 1 ? matches[0] : null;
        }

        private static bool IsExpired(string salePeriodTo, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(salePeriodTo))
                return false;
            return DateTime.TryParse(salePeriodTo, out var to) && to < now;
        }

        private static void ApplyKey(SqliteConnection conn)
        {
            var key = Environment.GetEnvironmentVariable("SHITTIM_EXCELDB_SQLCIPHER_KEY");
            if (string.IsNullOrWhiteSpace(key))
                key = Config.Instance.ServerConfiguration.ExcelDbSqlCipherKey;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA key = \"x'{key.Trim()}'\";";
            cmd.ExecuteNonQuery();
        }

        private static string GetExcelDbPath()
        {
            var configured = Environment.GetEnvironmentVariable("SHITTIM_CLIENT_EXCELDB_PATH");
            if (string.IsNullOrWhiteSpace(configured))
                configured = Config.Instance.ServerConfiguration.ClientExcelDbPath;
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            const string relative = @"BlueArchive_Data\StreamingAssets\PUB\Resource\Preload\TableBundles\ExcelDB.db";

            // Derive from the configured client-metadata install root (same install tree).
            var metaPath = Config.Instance.ServerConfiguration.ClientMetadataPath;
            if (!string.IsNullOrWhiteSpace(metaPath))
            {
                var idx = metaPath.IndexOf(@"\BlueArchive_Data", StringComparison.OrdinalIgnoreCase);
                if (idx > 0)
                {
                    var candidate = Path.Combine(metaPath[..idx], relative);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }

            // Any Steam library can hold the install.
            return SteamGameLocator.FindGameFile(relative) ?? string.Empty;
        }

        private readonly record struct BannerRow(long RowId, ShopRecruitExcelT Recruit, byte[] Bytes);
    }
}
