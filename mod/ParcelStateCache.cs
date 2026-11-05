using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace AiPhone
{
    internal static class ParcelStateCache
    {
        private const string ModId = "ai_phone";
        private const string CacheFileName = "parcel_state.json";

        private static CacheData data;
        private static bool loaded;

        internal static void OnGameLoaded(int day)
        {
            EnsureCurrentSave();
            foreach (int key in data.Days.Keys.Where(key => key >= day).ToArray())
            {
                data.Days.Remove(key);
            }

            data.Deliveries.RemoveAll(record => record.Day >= day);
            Save();
            Prototype.Log?.Log($"包裹缓存已回退到第 {day} 天开始状态。" );
        }

        internal static void MarkCallToday()
        {
            DayState state = CurrentDayState();
            if (state.Called)
            {
                return;
            }

            state.Called = true;
            Save();
        }

        internal static bool TryMarkDailySent()
        {
            DayState state = CurrentDayState();
            if (state.Called || state.DailySent)
            {
                return false;
            }

            state.DailySent = true;
            Save();
            return true;
        }

        internal static bool TryMarkPickupAttempt(int day)
        {
            EnsureCurrentSave();
            DayState state = GetDayState(day);
            if (state.PickupAttempted)
            {
                return false;
            }

            state.PickupAttempted = true;
            Save();
            return true;
        }

        internal static bool CanDeliverThisWeek(int day, int maximum)
        {
            EnsureCurrentSave();
            return GetDeliveredThisWeek(day) < Math.Max(0, maximum);
        }

        internal static int GetDeliveredThisWeek(int day)
        {
            EnsureCurrentSave();
            int week = WeekOf(day);
            return data.Deliveries.Count(record => WeekOf(record.Day) == week);
        }

        internal static void RecordDelivery(int day, string parcelId)
        {
            EnsureCurrentSave();
            data.Deliveries.Add(new DeliveryRecord { Day = day, ParcelId = parcelId ?? string.Empty });
            Save();
        }

        private static DayState CurrentDayState()
        {
            EnsureCurrentSave();
            return GetDayState(StoreStation.GetDayCounter());
        }

        private static DayState GetDayState(int day)
        {
            if (!data.Days.TryGetValue(day, out DayState state))
            {
                state = new DayState();
                data.Days[day] = state;
            }

            return state;
        }

        private static int WeekOf(int day)
        {
            return (Math.Max(1, day) - 1) / 7;
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
                data.Days ??= new Dictionary<int, DayState>();
                data.Deliveries ??= new List<DeliveryRecord>();
                return;
            }

            data = new CacheData { SaveKey = saveKey };
            Save();
            Prototype.Log?.Log("检测到存档切换：已清空该存档的包裹日状态。" );
        }

        private static string CurrentSaveKey()
        {
            PlayerStore store = PlayerStore.Instance;
            return $"{store.saveSlotId}:{store.runID ?? string.Empty}:{store.runNumber}";
        }

        private static CacheData Load()
        {
            try
            {
                if (!ModHelper.ModDataFileExists(ModId, CacheFileName))
                {
                    return null;
                }

                return JsonConvert.DeserializeObject<CacheData>(
                    ModHelper.ReadModDataFileText(ModId, CacheFileName));
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"读取包裹缓存失败，将重新建立：{exception.GetType().Name}: {exception.Message}" );
                return null;
            }
        }

        private static void Save()
        {
            try
            {
                ModHelper.WriteModDataFileText(ModId, CacheFileName, JsonConvert.SerializeObject(data));
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"保存包裹缓存失败：{exception.GetType().Name}: {exception.Message}" );
            }
        }

        private sealed class CacheData
        {
            [JsonProperty("saveKey")]
            public string SaveKey { get; set; }

            [JsonProperty("days")]
            public Dictionary<int, DayState> Days { get; set; } = new Dictionary<int, DayState>();

            [JsonProperty("deliveries")]
            public List<DeliveryRecord> Deliveries { get; set; } = new List<DeliveryRecord>();
        }

        private sealed class DayState
        {
            [JsonProperty("called")]
            public bool Called { get; set; }

            [JsonProperty("dailySent")]
            public bool DailySent { get; set; }

            [JsonProperty("pickupAttempted")]
            public bool PickupAttempted { get; set; }
        }

        private sealed class DeliveryRecord
        {
            [JsonProperty("day")]
            public int Day { get; set; }

            [JsonProperty("parcelId")]
            public string ParcelId { get; set; }
        }
    }
}
