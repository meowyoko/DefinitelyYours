using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace AiPhone
{
    internal static class PhoneStateCache
    {
        private const string ModId = "ai_phone";
        private const string CacheFileName = "call_state.json";

        private static CacheData data;
        private static bool loaded;

        internal static CallMemory BeginCall(int currentCash, bool includeRecorder)
        {
            EnsureCurrentSave();
            CallMemory memory = ReadCallMemory(includeRecorder);

            data.CashHistoryVersion = 1;
            data.CashHistory ??= new List<CashSnapshot>();
            data.CashHistory.Add(new CashSnapshot
            {
                Day = StoreStation.GetDayCounter(),
                Cash = currentCash
            });
            data.CashAtLastCall = currentCash;
            Save();
            return memory;
        }

        internal static CallMemory ReadCallMemory(bool includeRecorder)
        {
            EnsureCurrentSave();
            return new CallMemory
            {
                CashAtLastCall = data.CashAtLastCall,
                Recorder = includeRecorder
                    ? RecorderLedgerCache.ReadSinceBaseline()
                    : null
            };
        }

        internal static void CaptureRecorderBaseline()
        {
            RecorderLedgerCache.MarkBaseline();
        }

        internal static void CaptureNightReport()
        {
            if (!PlayerStore.HasInstance || PlayerStore.Instance.nightLogs == null)
            {
                return;
            }

            EnsureCurrentSave();
            int day = StoreStation.GetDayCounter();
            if (data.NightReportDay == day)
            {
                return;
            }

            data.NightReportDay = day;
            data.NightReport = new List<string>(PlayerStore.Instance.nightLogs);
            Save();
            Prototype.Log?.Log($"已缓存第 {day} 天夜间报告，共 {data.NightReport.Count} 条。");
        }

        internal static string[] GetNightReport()
        {
            if (!PlayerStore.HasInstance)
            {
                return null;
            }

            EnsureCurrentSave();
            if (PlayerStore.Instance.nightLogs != null && PlayerStore.Instance.nightLogs.Count > 0)
            {
                return PlayerStore.Instance.nightLogs.ToArray();
            }

            int day = StoreStation.GetDayCounter();
            if (data.NightReportDay != day || data.NightReport == null)
            {
                return null;
            }

            return data.NightReport.ToArray();
        }

        internal static int GetTransferredToday()
        {
            EnsureCurrentSave();
            EnsureTransferDay();
            return data.TransferredToday;
        }

        internal static void RecordTransfer(int amount)
        {
            if (amount <= 0)
            {
                return;
            }

            EnsureCurrentSave();
            EnsureTransferDay();
            data.TransferredToday += amount;
            Save();
        }

        internal static void RollBackOnLoad()
        {
            if (!PlayerStore.HasInstance)
            {
                return;
            }

            try
            {
                EnsureCurrentSave();
                int day = StoreStation.GetDayCounter();
                if (data.CashHistoryVersion < 1)
                {
                    // 旧缓存没有时间信息，无法判断其属于读档前还是读档后，只能安全清空一次。
                    data.CashHistory = new List<CashSnapshot>();
                    data.CashAtLastCall = null;
                    data.CashHistoryVersion = 1;
                }
                else
                {
                    data.CashHistory ??= new List<CashSnapshot>();
                    data.CashHistory.RemoveAll(entry => entry.Day >= day);
                    data.CashAtLastCall = data.CashHistory.Count == 0
                        ? null
                        : data.CashHistory[data.CashHistory.Count - 1].Cash;
                }

                data.TransferDay = null;
                data.TransferredToday = 0;
                RecorderLedgerCache.RollBackOnLoad(day);
                Save();
                Prototype.Log?.Log(
                    $"读档完成：已回退上次通话余额并清零本游戏日电话转账累计；day={day}；" +
                    $"cashAtLastCall={(data.CashAtLastCall.HasValue ? data.CashAtLastCall.Value.ToString() : "null")}。" );
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"读档后回退电话缓存失败：{exception.Message}");
            }
        }

        private static void EnsureCurrentSave()
        {
            if (!loaded)
            {
                loaded = true;
                data = Load() ?? new CacheData();
            }

            string saveKey = CurrentSaveKey();
            if (data.SaveKey == saveKey)
            {
                data.CashHistory ??= new List<CashSnapshot>();
                return;
            }

            data = new CacheData
            {
                SaveKey = saveKey,
                CashAtLastCall = null,
                CashHistoryVersion = 1,
                CashHistory = new List<CashSnapshot>(),
                NightReportDay = null,
                NightReport = null,
                TransferDay = null,
                TransferredToday = 0
            };
            Save();
            Prototype.Log?.Log("检测到存档切换：已清空上次通话余额；录音机小账已切换到对应存档。");
        }

        private static string CurrentSaveKey()
        {
            PlayerStore store = PlayerStore.Instance;
            return $"{store.saveSlotId}:{store.runID ?? string.Empty}:{store.runNumber}";
        }

        private static void EnsureTransferDay()
        {
            int day = StoreStation.GetDayCounter();
            if (data.TransferDay == day)
            {
                return;
            }

            data.TransferDay = day;
            data.TransferredToday = 0;
            Save();
        }

        private static CacheData Load()
        {
            try
            {
                if (!ModHelper.ModDataFileExists(ModId, CacheFileName))
                {
                    return null;
                }

                string json = ModHelper.ReadModDataFileText(ModId, CacheFileName);
                return JsonConvert.DeserializeObject<CacheData>(json);
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"读取通话缓存失败，将重新建立：{exception.GetType().Name}: {exception.Message}");
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
                Prototype.Log?.Error($"保存通话缓存失败：{exception.GetType().Name}: {exception.Message}");
            }
        }

        internal sealed class CallMemory
        {
            internal int? CashAtLastCall { get; set; }
            internal string[] Recorder { get; set; }
        }

        private sealed class CacheData
        {
            [JsonProperty("saveKey")]
            public string SaveKey { get; set; }

            [JsonProperty("cashAtLastCall")]
            public int? CashAtLastCall { get; set; }

            [JsonProperty("cashHistoryVersion")]
            public int CashHistoryVersion { get; set; }

            [JsonProperty("cashHistory")]
            public List<CashSnapshot> CashHistory { get; set; } = new List<CashSnapshot>();

            [JsonProperty("nightReportDay")]
            public int? NightReportDay { get; set; }

            [JsonProperty("nightReport")]
            public List<string> NightReport { get; set; }

            [JsonProperty("transferDay")]
            public int? TransferDay { get; set; }

            [JsonProperty("transferredToday")]
            public int TransferredToday { get; set; }
        }

        private sealed class CashSnapshot
        {
            [JsonProperty("day")]
            public int Day { get; set; }

            [JsonProperty("cash")]
            public int Cash { get; set; }
        }
    }
}
