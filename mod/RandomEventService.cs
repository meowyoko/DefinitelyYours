using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace AiPhone
{
    internal static class RandomEventService
    {
        private const string ModId = "ai_phone";
        private const string EventsFileName = "events.txt";
        private const string StateKey = "random_event_state";
        private static readonly Regex IdPattern = new Regex(
            "^[a-z0-9]+(?:-[a-z0-9]+)*$",
            RegexOptions.CultureInvariant);
        private static readonly Random Random = new Random();

        private static string catalogSource;
        private static Dictionary<string, EventDefinition> catalog =
            new Dictionary<string, EventDefinition>(StringComparer.Ordinal);
        private static bool catalogLoaded;
        private static string loadedSaveKey;
        private static EventState state;
        private static EventDefinition activeEvent;

        internal static void OnNewGameStarted(string _)
        {
            ResetSaveState();
        }

        internal static void OnGameLoaded()
        {
            ResetSaveState();
        }

        internal static RandomEventSelection BeginCall()
        {
            activeEvent = null;
            EnsureState();
            Dictionary<string, EventDefinition> definitions = ReadCatalog();
            int day = StoreStation.GetDayCounter();
            state.CallCount++;
            SaveState();

            string forcedId = TakeForcedEventId();
            if (!string.IsNullOrWhiteSpace(forcedId))
            {
                if (!definitions.TryGetValue(forcedId, out EventDefinition forced))
                {
                    LogDecision(day, "调试指定的 id 不存在", definitions.Count, null, false);
                    return null;
                }

                state.LastJudgedDay = day;
                SaveState();
                LogDecision(day, "调试强制", 1, forced, false);
                return Select(forced, isSeries: false, forced: true);
            }

            if (!(Prototype.RandomEventsEnabledSetting?.Value ?? true))
            {
                LogDecision(day, "总开关关闭", 0, null, false);
                return null;
            }

            if (state.LastJudgedDay == day)
            {
                LogDecision(day, "本日已经判定过", 0, null, false);
                return null;
            }

            state.LastJudgedDay = day;
            SaveState();

            PendingSeries pending = state.PendingSeries
                .Where(item => item != null && item.DueDay <= day)
                .OrderBy(item => item.DueDay)
                .ThenBy(item => item.EventId, StringComparer.Ordinal)
                .FirstOrDefault(item => definitions.ContainsKey(item.EventId));
            if (pending != null)
            {
                EventDefinition episode = definitions[pending.EventId];
                if (episode.Type == EventType.Unreachable && !CanUseUnreachable(day, ignoreFrequency: true))
                {
                    LogDecision(day, "连续剧打不通集受保护，顺延", 1, null, true);
                    return null;
                }

                LogDecision(day, "连续剧到期", 1, episode, true);
                return Select(episode, isSeries: true, forced: false);
            }

            int minimumGap = Math.Max(0, Prototype.EventMinCallGapSetting?.Value ?? 2);
            if (state.LastEventCall.HasValue
                && state.CallCount - state.LastEventCall.Value <= minimumGap)
            {
                LogDecision(day, $"距上次事件不足 {minimumGap} 通", 0, null, false);
                return null;
            }

            List<EventDefinition> eligible = definitions.Values
                .Where(item => item.Entry && day >= item.MinDay && CanRepeat(item, day))
                .ToList();
            int chance = ClampPercent(Prototype.EventChanceSetting?.Value ?? 33);
            if (eligible.Count == 0 || Random.Next(100) >= chance)
            {
                LogDecision(day, eligible.Count == 0 ? "没有候选" : "概率未命中", eligible.Count, null, false);
                return null;
            }

            List<EventDefinition> talk = eligible.Where(item => item.Type == EventType.Talk).ToList();
            List<EventDefinition> unreachable = eligible
                .Where(item => item.Type == EventType.Unreachable)
                .ToList();
            bool chooseUnreachable = unreachable.Count > 0
                && CanUseUnreachable(day, ignoreFrequency: false)
                && Random.Next(100) < ClampPercent(Prototype.UnreachableChanceSetting?.Value ?? 10);
            List<EventDefinition> pool = chooseUnreachable ? unreachable : talk;
            if (pool.Count == 0)
            {
                LogDecision(day, "本次类型池没有候选", eligible.Count, null, false);
                return null;
            }

            EventDefinition selected = WeightedPick(pool);
            LogDecision(day, "随机命中", eligible.Count, selected, false);
            return Select(selected, isSeries: false, forced: false);
        }

        internal static bool ConfirmUsed(string eventId)
        {
            string normalized = (eventId ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            if (activeEvent == null || !string.Equals(activeEvent.Id, normalized, StringComparison.Ordinal))
            {
                Prototype.Log?.Error(
                    $"忽略不匹配的 event_used：收到 {normalized}，当前事件为 {activeEvent?.Id ?? "无"}。" );
                return false;
            }

            RecordUsed(activeEvent, StoreStation.GetDayCounter());
            activeEvent = null;
            return true;
        }

        internal static MissedEventPayload[] ReadMissedEvents()
        {
            EnsureState();
            return state.MissedEvents
                .Where(item => item != null)
                .Select(item => new MissedEventPayload
                {
                    Id = item.Id,
                    Text = item.Text,
                    Day = item.Day,
                    Time = item.Time
                })
                .ToArray();
        }

        internal static void AcknowledgeMissedEvents()
        {
            EnsureState();
            if (state.MissedEvents.Count == 0)
            {
                return;
            }

            int count = state.MissedEvents.Count;
            state.MissedEvents.Clear();
            SaveState();
            Prototype.Log?.Log($"已把 {count} 条未接通记录交给电话插件。" );
        }

        internal static void EndCall()
        {
            activeEvent = null;
        }

        private static RandomEventSelection Select(EventDefinition definition, bool isSeries, bool forced)
        {
            if (definition.Type == EventType.Unreachable)
            {
                RecordUsed(definition, StoreStation.GetDayCounter());
                return new RandomEventSelection
                {
                    Event = ToPayload(definition),
                    IsUnreachable = true,
                    IsSeries = isSeries,
                    IsForced = forced
                };
            }

            activeEvent = definition;
            return new RandomEventSelection
            {
                Event = ToPayload(definition),
                IsUnreachable = false,
                IsSeries = isSeries,
                IsForced = forced
            };
        }

        private static void RecordUsed(EventDefinition definition, int day)
        {
            EnsureState();
            state.History.Add(new EventOccurrence
            {
                Id = definition.Id,
                Day = day,
                Type = definition.Type == EventType.Unreachable ? "unreachable" : "talk"
            });
            state.LastEventCall = state.CallCount;
            state.PendingSeries.RemoveAll(item =>
                item != null && string.Equals(item.EventId, definition.Id, StringComparison.Ordinal));

            if (!string.IsNullOrWhiteSpace(definition.NextId))
            {
                state.PendingSeries.RemoveAll(item =>
                    item != null && string.Equals(item.SourceId, definition.Id, StringComparison.Ordinal));
                state.PendingSeries.Add(new PendingSeries
                {
                    SourceId = definition.Id,
                    EventId = definition.NextId,
                    DueDay = day + Math.Max(0, definition.NextDelay)
                });
            }

            if (definition.Type == EventType.Unreachable)
            {
                state.LastUnreachableDay = day;
                state.MissedEvents.Add(new MissedEventRecord
                {
                    Id = definition.Id,
                    Text = definition.Text,
                    Day = day,
                    Time = ReadGameTime()
                });
                if (state.MissedEvents.Count > 20)
                {
                    state.MissedEvents.RemoveRange(0, state.MissedEvents.Count - 20);
                }
            }

            SaveState();
            Prototype.Log?.Log($"随机事件已用：id={definition.Id}；day={day}；type={definition.Type.ToString().ToLowerInvariant()}。" );
        }

        private static bool CanUseUnreachable(int day, bool ignoreFrequency)
        {
            if (!(Prototype.UnreachableEnabledSetting?.Value ?? true))
            {
                return false;
            }

            if (state.LastUnreachableDay == day - 1)
            {
                return false;
            }

            if (Prototype.RentProtectionSetting?.Value ?? false)
            {
                int? due = GameContextReader.ReadRentDueInDays();
                int? rent = GameContextReader.ReadRentAmount();
                int cash = PlayerStore.Instance.GetCash();
                if (due == 0 || (rent.HasValue && cash < rent.Value))
                {
                    return false;
                }
            }

            return ignoreFrequency || ClampPercent(Prototype.UnreachableChanceSetting?.Value ?? 10) > 0;
        }

        private static bool CanRepeat(EventDefinition definition, int day)
        {
            if (state.PendingSeries.Any(item => item != null
                && string.Equals(item.SourceId, definition.Id, StringComparison.Ordinal)))
            {
                return false;
            }

            EventOccurrence latest = state.History
                .Where(item => item != null && string.Equals(item.Id, definition.Id, StringComparison.Ordinal))
                .OrderByDescending(item => item.Day)
                .FirstOrDefault();
            if (latest == null)
            {
                return true;
            }

            return definition.RepeatCooldown.HasValue
                && day - latest.Day >= definition.RepeatCooldown.Value;
        }

        private static EventDefinition WeightedPick(IList<EventDefinition> items)
        {
            int total = items.Sum(item => Math.Max(1, item.Weight));
            int roll = Random.Next(total);
            foreach (EventDefinition item in items)
            {
                roll -= Math.Max(1, item.Weight);
                if (roll < 0)
                {
                    return item;
                }
            }

            return items[items.Count - 1];
        }

        private static PhoneEventPayload ToPayload(EventDefinition definition)
        {
            return new PhoneEventPayload
            {
                Id = definition.Id,
                Type = definition.Type == EventType.Unreachable ? "unreachable" : "talk",
                Text = definition.Text
            };
        }

        private static Dictionary<string, EventDefinition> ReadCatalog()
        {
            string source = null;
            try
            {
                if (ModHelper.ModFileExists(ModId, EventsFileName))
                {
                    source = ModHelper.ReadModFileText(ModId, EventsFileName) ?? string.Empty;
                }
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"读取 events.txt 失败：{exception.GetType().Name}: {exception.Message}" );
            }

            if (catalogLoaded && string.Equals(source, catalogSource, StringComparison.Ordinal))
            {
                return catalog;
            }

            catalogLoaded = true;
            catalogSource = source;
            catalog = ParseCatalog(source ?? string.Empty);
            Prototype.Log?.Log(source == null
                ? "未找到 events.txt，随机事件池为空。"
                : $"随机事件池已载入：{catalog.Count} 条。" );
            return catalog;
        }

        private static Dictionary<string, EventDefinition> ParseCatalog(string source)
        {
            var result = new Dictionary<string, EventDefinition>(StringComparer.Ordinal);
            string normalized = (source ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            string[] blocks = Regex.Split(normalized, "(?m)^\\s*---\\s*$");
            for (int blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
            {
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string rawLine in blocks[blockIndex].Split('\n'))
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int separator = line.IndexOf(':');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    fields[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
                }

                if (fields.Count == 0)
                {
                    continue;
                }

                fields.TryGetValue("id", out string id);
                fields.TryGetValue("type", out string typeText);
                fields.TryGetValue("text", out string text);
                id = (id ?? string.Empty).Trim();
                typeText = (typeText ?? string.Empty).Trim().ToLowerInvariant();
                text = (text ?? string.Empty).Trim();
                if (!IdPattern.IsMatch(id) || (typeText != "talk" && typeText != "unreachable")
                    || string.IsNullOrWhiteSpace(text))
                {
                    Prototype.Log?.Error($"events.txt 第 {blockIndex + 1} 条缺少或写错 id/type/text，已跳过。" );
                    continue;
                }

                if (result.ContainsKey(id))
                {
                    Prototype.Log?.Error($"events.txt id 重复：{id}，保留第一条。" );
                    continue;
                }

                var definition = new EventDefinition
                {
                    Id = id,
                    Type = typeText == "unreachable" ? EventType.Unreachable : EventType.Talk,
                    Text = text,
                    Weight = ParseInt(fields, "weight", 1, 1, 5),
                    MinDay = ParseInt(fields, "min_day", 1, 1, int.MaxValue),
                    Entry = ParseYesNo(fields, "entry", true),
                    Callable = ParseYesNo(fields, "callable", false)
                };
                ParseRepeat(fields, definition);
                ParseNext(fields, definition);
                result.Add(id, definition);
            }

            foreach (EventDefinition definition in result.Values)
            {
                if (!string.IsNullOrWhiteSpace(definition.NextId)
                    && !result.ContainsKey(definition.NextId))
                {
                    Prototype.Log?.Error(
                        $"events.txt 事件 {definition.Id} 的 next 指向不存在的 id：{definition.NextId}，已忽略。" );
                    definition.NextId = null;
                    definition.NextDelay = 0;
                }
            }

            return result;
        }

        private static int ParseInt(
            IDictionary<string, string> fields,
            string key,
            int fallback,
            int minimum,
            int maximum)
        {
            if (!fields.TryGetValue(key, out string text) || !int.TryParse(text, out int value))
            {
                return fallback;
            }

            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static bool ParseYesNo(IDictionary<string, string> fields, string key, bool fallback)
        {
            if (!fields.TryGetValue(key, out string text))
            {
                return fallback;
            }

            if (string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(text, "no", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        private static void ParseRepeat(IDictionary<string, string> fields, EventDefinition definition)
        {
            if (!fields.TryGetValue("repeat", out string repeat)
                || string.Equals(repeat.Trim(), "once", StringComparison.OrdinalIgnoreCase))
            {
                definition.RepeatCooldown = null;
                return;
            }

            string[] parts = repeat.Split(':');
            if (parts.Length == 2
                && string.Equals(parts[0].Trim(), "cooldown", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(parts[1].Trim(), out int days)
                && days >= 0)
            {
                definition.RepeatCooldown = days;
                return;
            }

            Prototype.Log?.Error($"events.txt 事件 {definition.Id} 的 repeat 无效，按 once 处理。" );
            definition.RepeatCooldown = null;
        }

        private static void ParseNext(IDictionary<string, string> fields, EventDefinition definition)
        {
            if (!fields.TryGetValue("next", out string next) || string.IsNullOrWhiteSpace(next))
            {
                return;
            }

            Match match = Regex.Match(next.Trim(), "^([a-z0-9]+(?:-[a-z0-9]+)*)\\s*@\\s*(\\d+)$");
            if (!match.Success || !int.TryParse(match.Groups[2].Value, out int delay))
            {
                Prototype.Log?.Error($"events.txt 事件 {definition.Id} 的 next 无效，已忽略。" );
                return;
            }

            definition.NextId = match.Groups[1].Value;
            definition.NextDelay = delay;
        }

        private static string TakeForcedEventId()
        {
            string id = Prototype.ForcedEventSetting?.Value?.Trim();
            if (string.IsNullOrEmpty(id))
            {
                if (!string.IsNullOrEmpty(state.ConsumedForcedId))
                {
                    state.ConsumedForcedId = null;
                    SaveState();
                }

                return null;
            }

            if (string.Equals(state.ConsumedForcedId, id, StringComparison.Ordinal))
            {
                return null;
            }

            state.ConsumedForcedId = id;
            SaveState();
            return id;
        }

        private static string ReadGameTime()
        {
            try
            {
                TurnManager clock = GameMaster.current?.turnManager;
                return clock == null
                    ? "未知时间"
                    : $"{clock.GetHoursFormatted()}:{clock.GetMinutesFormatted()}";
            }
            catch
            {
                return "未知时间";
            }
        }

        private static int ClampPercent(int value)
        {
            return Math.Max(0, Math.Min(100, value));
        }

        private static void LogDecision(
            int day,
            string reason,
            int candidates,
            EventDefinition selected,
            bool isSeries)
        {
            Prototype.Log?.Log(
                $"随机事件判定：day={day}；reason={reason}；candidates={candidates}；" +
                $"selected={selected?.Id ?? "none"}；series={isSeries}。" );
        }

        private static void ResetSaveState()
        {
            loadedSaveKey = null;
            state = null;
            activeEvent = null;
        }

        private static void EnsureState()
        {
            string saveKey = CurrentSaveKey();
            if (state != null && string.Equals(loadedSaveKey, saveKey, StringComparison.Ordinal))
            {
                NormalizeState(state);
                return;
            }

            loadedSaveKey = saveKey;
            state = null;
            try
            {
                string json = ModHelper.GetModData(ModId, StateKey, null);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    state = JsonConvert.DeserializeObject<EventState>(json);
                }
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"读取随机事件存档失败，将重新建立：{exception.GetType().Name}: {exception.Message}" );
            }

            state ??= new EventState();
            NormalizeState(state);
        }

        private static void NormalizeState(EventState value)
        {
            value.History ??= new List<EventOccurrence>();
            value.PendingSeries ??= new List<PendingSeries>();
            value.MissedEvents ??= new List<MissedEventRecord>();
        }

        private static void SaveState()
        {
            try
            {
                ModHelper.SetModData(ModId, StateKey, JsonConvert.SerializeObject(state));
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"保存随机事件存档失败：{exception.GetType().Name}: {exception.Message}" );
            }
        }

        private static string CurrentSaveKey()
        {
            return PhoneSaveContext.CurrentSaveId();
        }

        private enum EventType
        {
            Talk,
            Unreachable
        }

        private sealed class EventDefinition
        {
            public string Id { get; set; }
            public int Weight { get; set; }
            public int MinDay { get; set; }
            public int? RepeatCooldown { get; set; }
            public EventType Type { get; set; }
            public bool Entry { get; set; }
            public string NextId { get; set; }
            public int NextDelay { get; set; }
            public bool Callable { get; set; }
            public string Text { get; set; }
        }

        private sealed class EventState
        {
            [JsonProperty("callCount")]
            public int CallCount { get; set; }

            [JsonProperty("lastJudgedDay")]
            public int? LastJudgedDay { get; set; }

            [JsonProperty("lastEventCall")]
            public int? LastEventCall { get; set; }

            [JsonProperty("lastUnreachableDay")]
            public int? LastUnreachableDay { get; set; }

            [JsonProperty("consumedForcedId")]
            public string ConsumedForcedId { get; set; }

            [JsonProperty("history")]
            public List<EventOccurrence> History { get; set; } = new List<EventOccurrence>();

            [JsonProperty("pendingSeries")]
            public List<PendingSeries> PendingSeries { get; set; } = new List<PendingSeries>();

            [JsonProperty("missedEvents")]
            public List<MissedEventRecord> MissedEvents { get; set; } = new List<MissedEventRecord>();
        }

        private sealed class EventOccurrence
        {
            [JsonProperty("id")]
            public string Id { get; set; }

            [JsonProperty("day")]
            public int Day { get; set; }

            [JsonProperty("type")]
            public string Type { get; set; }
        }

        private sealed class PendingSeries
        {
            [JsonProperty("sourceId")]
            public string SourceId { get; set; }

            [JsonProperty("eventId")]
            public string EventId { get; set; }

            [JsonProperty("dueDay")]
            public int DueDay { get; set; }
        }

        private sealed class MissedEventRecord
        {
            [JsonProperty("id")]
            public string Id { get; set; }

            [JsonProperty("text")]
            public string Text { get; set; }

            [JsonProperty("day")]
            public int Day { get; set; }

            [JsonProperty("time")]
            public string Time { get; set; }
        }
    }

    internal sealed class RandomEventSelection
    {
        internal PhoneEventPayload Event { get; set; }
        internal bool IsUnreachable { get; set; }
        internal bool IsSeries { get; set; }
        internal bool IsForced { get; set; }
    }

    internal sealed class PhoneEventPayload
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("text")]
        public string Text { get; set; }
    }

    internal sealed class MissedEventPayload
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("text")]
        public string Text { get; set; }

        [JsonProperty("day")]
        public int Day { get; set; }

        [JsonProperty("time")]
        public string Time { get; set; }
    }
}
