using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace AiPhone
{
    internal static class RecorderLedgerCache
    {
        private const string ModId = "ai_phone";
        private const string CacheFileName = "recorder_state.json";
        private const int MaxResponseEntries = 30;
        private const int RetainedSentEntries = 200;

        private static CacheRoot data;
        private static bool loaded;

        internal static void Record(string text)
        {
            if (!PlayerStore.HasInstance)
            {
                return;
            }

            try
            {
                SaveLedger ledger = GetCurrentLedger();
                ledger.Entries.Add(new RecorderEntry
                {
                    Day = StoreStation.GetDayCounter(),
                    Text = text ?? string.Empty
                });
                Save();
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"记录录音机小账失败：{exception.GetType().Name}: {exception.Message}");
            }
        }

        internal static string[] ReadSinceBaseline()
        {
            try
            {
                SaveLedger ledger = GetCurrentLedger();
                int baseline = Math.Max(0, Math.Min(ledger.Baseline, ledger.Entries.Count));
                if (baseline >= ledger.Entries.Count)
                {
                    return Array.Empty<string>();
                }

                // 分隔线也占协议的 30 条额度，因此最多先取 30 条原始记录，再从前端收缩。
                int start = Math.Max(baseline, ledger.Entries.Count - MaxResponseEntries);
                List<string> result = Format(ledger.Entries, start);
                while (result.Count > MaxResponseEntries && start < ledger.Entries.Count)
                {
                    start++;
                    result = Format(ledger.Entries, start);
                }

                return result.ToArray();
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"读取录音机小账失败：{exception.GetType().Name}: {exception.Message}");
                return Array.Empty<string>();
            }
        }

        internal static void MarkBaseline()
        {
            try
            {
                SaveLedger ledger = GetCurrentLedger();
                ledger.Baseline = ledger.Entries.Count;
                PruneSentEntries(ledger);
                Save();
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"推进录音机小账基线失败：{exception.GetType().Name}: {exception.Message}");
            }
        }

        internal static void RollBackOnLoad(int day)
        {
            try
            {
                SaveLedger ledger = GetCurrentLedger();
                int removed = ledger.Entries.RemoveAll(entry => entry.Day >= day);
                ledger.Baseline = Math.Min(ledger.Baseline, ledger.Entries.Count);
                PruneSentEntries(ledger);
                Save();
                Prototype.Log?.Log(
                    $"读档完成：录音机小账已回退到第 {day} 天开始；删除 {removed} 条，保留 {ledger.Entries.Count} 条。" );
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"读档后回退录音机小账失败：{exception.GetType().Name}: {exception.Message}");
            }
        }

        private static List<string> Format(List<RecorderEntry> entries, int start)
        {
            var result = new List<string>();
            int? currentDay = null;
            for (int index = start; index < entries.Count; index++)
            {
                RecorderEntry entry = entries[index];
                if (currentDay != entry.Day)
                {
                    currentDay = entry.Day;
                    result.Add($"—— 第{entry.Day}天 ——");
                }

                result.Add(entry.Text ?? string.Empty);
            }

            return result;
        }

        private static void PruneSentEntries(SaveLedger ledger)
        {
            int excess = Math.Max(0, ledger.Entries.Count - RetainedSentEntries);
            int removeCount = Math.Min(excess, ledger.Baseline);
            if (removeCount <= 0)
            {
                return;
            }

            ledger.Entries.RemoveRange(0, removeCount);
            ledger.Baseline -= removeCount;
        }

        private static SaveLedger GetCurrentLedger()
        {
            EnsureLoaded();
            string saveKey = CurrentSaveKey();
            if (!data.Saves.TryGetValue(saveKey, out SaveLedger ledger) || ledger == null)
            {
                ledger = new SaveLedger();
                data.Saves[saveKey] = ledger;
            }

            ledger.Entries ??= new List<RecorderEntry>();
            ledger.Baseline = Math.Max(0, Math.Min(ledger.Baseline, ledger.Entries.Count));
            return ledger;
        }

        private static string CurrentSaveKey()
        {
            PlayerStore store = PlayerStore.Instance;
            return $"{store.saveSlotId}:{store.runID ?? string.Empty}:{store.runNumber}";
        }

        private static void EnsureLoaded()
        {
            if (loaded)
            {
                return;
            }

            loaded = true;
            data = Load() ?? new CacheRoot();
            data.Saves ??= new Dictionary<string, SaveLedger>(StringComparer.Ordinal);
        }

        private static CacheRoot Load()
        {
            try
            {
                if (!ModHelper.ModDataFileExists(ModId, CacheFileName))
                {
                    return null;
                }

                string json = ModHelper.ReadModDataFileText(ModId, CacheFileName);
                return JsonConvert.DeserializeObject<CacheRoot>(json);
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"读取录音机小账失败，将重新建立：{exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        private static void Save()
        {
            try
            {
                string json = JsonConvert.SerializeObject(data);
                ModHelper.WriteModDataFileText(ModId, CacheFileName, json);
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"保存录音机小账失败：{exception.GetType().Name}: {exception.Message}");
            }
        }

        private sealed class CacheRoot
        {
            [JsonProperty("saves")]
            public Dictionary<string, SaveLedger> Saves { get; set; } =
                new Dictionary<string, SaveLedger>(StringComparer.Ordinal);
        }

        private sealed class SaveLedger
        {
            [JsonProperty("baseline")]
            public int Baseline { get; set; }

            [JsonProperty("entries")]
            public List<RecorderEntry> Entries { get; set; } = new List<RecorderEntry>();
        }

        private sealed class RecorderEntry
        {
            [JsonProperty("day")]
            public int Day { get; set; }

            [JsonProperty("text")]
            public string Text { get; set; }
        }
    }
}
