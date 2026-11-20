using System;
using Newtonsoft.Json;

namespace AiPhone
{
    internal class PhoneSaveContext
    {
        internal static string CurrentTimeline { get; private set; } = Guid.NewGuid().ToString("N");

        [JsonProperty("saveId")]
        public string SaveId { get; set; }

        [JsonProperty("timeline")]
        public string Timeline { get; set; }

        [JsonProperty("sessionReset", NullValueHandling = NullValueHandling.Ignore)]
        public string SessionReset { get; set; }

        internal static void StartTimeline()
        {
            CurrentTimeline = Guid.NewGuid().ToString("N");
        }

        internal static string CurrentSaveId()
        {
            PlayerStore store = PlayerStore.Instance;
            return $"{store.saveSlotId}:{store.runID ?? string.Empty}:{store.runNumber}";
        }

        internal static bool HasStableSaveId()
        {
            PlayerStore store = PlayerStore.Instance;
            return store.saveSlotId >= 0 && !string.IsNullOrWhiteSpace(store.runID);
        }

        internal static T Capture<T>(T request, bool includeReset = false) where T : PhoneSaveContext
        {
            request.SaveId = CurrentSaveId();
            request.Timeline = CurrentTimeline;
            if (includeReset)
            {
                string token = Prototype.SessionResetSetting?.Value?.Trim();
                int separator = token?.LastIndexOf('|') ?? -1;
                if (separator >= 0)
                {
                    token = token.Substring(0, separator) == request.SaveId ? token.Substring(separator + 1) : null;
                }
                request.SessionReset = string.IsNullOrEmpty(token) ? null : token;
            }

            return request;
        }
    }
}
