using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace AiPhone
{
    internal static class Prototype
    {
        private const string ModId = "ai_phone";
        private const string TokenHeader = "X-Phone-Token";
        private const string SoundKey = "remote_voice";
        private const string FreeInputWindowId = "ai_phone.free_input";

        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        private static readonly ConcurrentQueue<Action> MainThreadActions = new ConcurrentQueue<Action>();
        private static readonly ConcurrentQueue<Action> NextFrameActions = new ConcurrentQueue<Action>();
        private static ChoiceOption[] activeOptions = Array.Empty<ChoiceOption>();
        private static int requestEpoch;
        private static bool isActiveCall;
        private static bool isWaitingForResponse;
        private static bool serverWillHangUp;
        private static AudioClip dynamicClip;
        private static float dynamicClipEndsAt;
        private static bool hangUpWhenAudioStops;

        internal static ModLog Log;
        internal static ConfigEntry<bool> TextOnlySetting;
        internal static ConfigEntry<bool> SendRecorderSetting;
        internal static ConfigEntry<int> SingleTransferLimitSetting;
        internal static ConfigEntry<int> DailyTransferLimitSetting;
        internal static ConfigEntry<string> AiNameSetting;
        internal static ConfigEntry<int> PhoneNumberSetting;
        internal static ConfigEntry<int> DialogueTextSizeSetting;
        internal static ConfigEntry<string> NoteSignatureSetting;
        internal static ConfigEntry<int> ParcelValueLimitSetting;
        internal static ConfigEntry<int> ParcelPerWeekLimitSetting;
        internal static ConfigEntry<bool> AllowContrabandSetting;
        internal static ConfigEntry<bool> AllowStolenSetting;
        internal static Dialogue ActiveChoiceDialogue;

        internal static string AiName
        {
            get
            {
                string value = AiNameSetting?.Value?.Trim();
                return string.IsNullOrWhiteSpace(value) ? "Assistant" : value;
            }
        }

        internal static long PhoneNumber => Math.Max(1, PhoneNumberSetting?.Value ?? 10000);

        internal static float DialogueTextScale =>
            Mathf.Clamp(DialogueTextSizeSetting?.Value ?? 100, 100, 160) / 100f;

        internal static string NoteSignature
        {
            get
            {
                string value = NoteSignatureSetting?.Value?.Trim();
                return string.IsNullOrWhiteSpace(value) ? AiName : value;
            }
        }

        internal static void BeginCall()
        {
            isActiveCall = true;
            serverWillHangUp = false;
            ParcelStateCache.MarkCallToday();
            int epoch = ++requestEpoch;
            ActiveChoiceDialogue = null;
            activeOptions = Array.Empty<ChoiceOption>();
            CloseFreeInputWindow();
            ReleaseDynamicClip(true);
            ShowWaiting();

            if (!TryLoadConfig(out PhoneConfig config, out string configError))
            {
                FailOnMainThread(epoch, configError);
                return;
            }

            bool textOnly = TextOnlySetting?.Value ?? false;
            CallRequest body = CreateContextRequest(beginCall: true);

            Log?.Log(
                $"发出请求：POST /phone/call；textOnly={textOnly}；cash={body.State.Cash}；day={body.State.Day ?? "null"}；" +
                $"cashAtLastCall={(body.CashAtLastCall.HasValue ? body.CashAtLastCall.Value.ToString() : "null")}；" +
                $"recorder={(body.Recorder == null ? "omitted" : body.Recorder.Length.ToString())}");
            _ = PostAsync(epoch, config, "/phone/call", body, textOnly);
        }

        internal static object CreateDailyRequest()
        {
            return CreateContextRequest(beginCall: false);
        }

        private static CallRequest CreateContextRequest(bool beginCall)
        {
            bool sendRecorder = SendRecorderSetting?.Value ?? true;
            int cash = 0;
            object day = null;
            try
            {
                cash = PlayerStore.Instance.GetCash();
                day = StoreStation.GetDayCounter();
            }
            catch (Exception exception)
            {
                Log?.Error($"读取通话上下文失败：{exception.GetType().Name}: {exception.Message}");
            }

            PhoneStateCache.CallMemory memory = beginCall
                ? PhoneStateCache.BeginCall(cash, sendRecorder)
                : PhoneStateCache.ReadCallMemory(sendRecorder);
            RunStats runStats = RunStats.Current;
            return new CallRequest
            {
                Contact = AiName,
                Number = PhoneNumber.ToString(),
                TextOnly = TextOnlySetting?.Value ?? false,
                State = new CallState
                {
                    Cash = cash,
                    Day = day,
                    StoreName = PlayerStore.Instance.storeName,
                    PlayerName = PlayerStore.Instance.playerName,
                    Reputation = ReadReputation(),
                    SalesToday = runStats?.salesToday,
                    CustomersServedTotal = runStats?.customersServed,
                    ShopOpen = GameContextReader.ReadShopOpen(),
                    StoreState = GameContextReader.ReadStoreState(),
                    RentDueInDays = GameContextReader.ReadRentDueInDays(),
                    RentAmount = GameContextReader.ReadRentAmount(),
                    TransferredToday = PhoneStateCache.GetTransferredToday(),
                    TransferMaxSingle = GetTransferMaxSingle(),
                    TransferMaxDaily = GetTransferMaxDaily(),
                    ParcelsThisWeek = ParcelStateCache.GetDeliveredThisWeek(StoreStation.GetDayCounter()),
                    ParcelMaxPerWeek = GetParcelMaxPerWeek(),
                    ParcelMaxValue = GetParcelMaxValue()
                },
                CashAtLastCall = memory.CashAtLastCall,
                Recorder = memory.Recorder,
                Notices = GameContextReader.ReadNotices(),
                Calendar = GameContextReader.ReadCalendar(),
                NightReport = GameContextReader.ReadNightReport(),
                GiftHints = ParcelService.BuildGiftHints()
            };
        }

        internal static bool IsActiveChoice(Dialogue dialogue)
        {
            return isActiveCall && dialogue == ActiveChoiceDialogue;
        }

        internal static void Choose(int optionIndex)
        {
            if (optionIndex < 0 || optionIndex >= activeOptions.Length)
            {
                int invalidEpoch = ++requestEpoch;
                DialoguePhoneUIManager.Instance.HideChoiceBubble();
                FailOnMainThread(invalidEpoch, $"无效选项序号：{optionIndex}");
                return;
            }

            ChoiceOption option = activeOptions[optionIndex];
            if (option.IsFreeInput)
            {
                OpenFreeInputWindow();
                return;
            }

            Log?.Log($"选择了第 {option.ServerIndex.GetValueOrDefault() + 1} 项：{option.Text}");
            WritePlayerRecorderLine(option.Text);
            DialoguePhoneUIManager.Instance.HideChoiceBubble();
            SendChoice(option.ServerIndex, option.Text);
        }

        internal static void RunMainThreadActions()
        {
            while (NextFrameActions.TryDequeue(out Action nextFrameAction))
            {
                MainThreadActions.Enqueue(nextFrameAction);
            }

            while (MainThreadActions.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    Log?.Error($"主线程更新失败：{exception.GetType().Name}: {exception.Message}");
                }
            }

            ReleaseFinishedClip();
        }

        internal static void EnqueueMainThread(Action action)
        {
            if (action != null)
            {
                MainThreadActions.Enqueue(action);
            }
        }

        internal static void EnqueueNextFrame(Action action)
        {
            if (action != null)
            {
                NextFrameActions.Enqueue(action);
            }
        }

        internal static void CancelActiveCall()
        {
            if (!isActiveCall && dynamicClip == null)
            {
                return;
            }

            if (isActiveCall && !serverWillHangUp)
            {
                NotifyManualHangup();
            }

            PhoneStateCache.CaptureRecorderBaseline();
            isActiveCall = false;
            isWaitingForResponse = false;
            serverWillHangUp = false;
            requestEpoch++;
            ActiveChoiceDialogue = null;
            activeOptions = Array.Empty<ChoiceOption>();
            CloseFreeInputWindow();
            ReleaseDynamicClip(true);
        }

        private static void SendChoice(int? index, string text)
        {
            int epoch = ++requestEpoch;
            ActiveChoiceDialogue = null;
            activeOptions = Array.Empty<ChoiceOption>();
            CloseFreeInputWindow();
            ReleaseDynamicClip(true);
            ShowWaiting();

            if (!TryLoadConfig(out PhoneConfig config, out string configError))
            {
                FailOnMainThread(epoch, configError);
                return;
            }

            bool textOnly = TextOnlySetting?.Value ?? false;
            var body = new ChoiceRequest
            {
                Index = index,
                Text = text,
                TextOnly = textOnly,
                State = CreateChoiceState()
            };
            Log?.Log($"发出请求：POST /phone/choice；index={(index.HasValue ? index.Value.ToString() : "null")}；textOnly={textOnly}");
            _ = PostAsync(epoch, config, "/phone/choice", body, textOnly);
        }

        private static async Task PostAsync(int epoch, PhoneConfig config, string path, object requestBody, bool textOnly)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                string json = JsonConvert.SerializeObject(requestBody);
                using (var request = new HttpRequestMessage(HttpMethod.Post, config.BaseUrl + path))
                {
                    request.Headers.TryAddWithoutValidation(TokenHeader, config.Token);
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    using (HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false))
                    {
                        string responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                        }

                        PhoneResponse phoneResponse = JsonConvert.DeserializeObject<PhoneResponse>(responseJson);
                        if (phoneResponse == null || string.IsNullOrWhiteSpace(phoneResponse.Text))
                        {
                            throw new InvalidOperationException("响应缺少 text");
                        }

                        Log?.Log($"收到响应：POST {path}，耗时 {stopwatch.ElapsedMilliseconds} ms");
                        byte[] audioBytes = null;
                        string audioWarning = null;

                        if (!textOnly && phoneResponse.Audio != null && !string.IsNullOrWhiteSpace(phoneResponse.Audio.Id))
                        {
                            Stopwatch audioStopwatch = Stopwatch.StartNew();
                            try
                            {
                                audioBytes = await DownloadAudioAsync(config, phoneResponse.Audio.Id).ConfigureAwait(false);
                                Log?.Log($"收到音频：{audioBytes.Length} bytes，下载耗时 {audioStopwatch.ElapsedMilliseconds} ms");
                            }
                            catch (Exception exception)
                            {
                                audioWarning = $"音频下载失败，改为纯文字：{exception.GetType().Name}: {exception.Message}，耗时 {audioStopwatch.ElapsedMilliseconds} ms";
                            }
                        }

                        stopwatch.Stop();
                        byte[] completedAudio = audioBytes;
                        string completedAudioWarning = audioWarning;
                        MainThreadActions.Enqueue(() =>
                        {
                            if (!IsCurrent(epoch))
                            {
                                return;
                            }

                            if (!string.IsNullOrWhiteSpace(completedAudioWarning))
                            {
                                Log?.Error(completedAudioWarning);
                            }

                            ShowResponse(phoneResponse, completedAudio);
                        });
                    }
                }
            }
            catch (TaskCanceledException)
            {
                stopwatch.Stop();
                FailOnMainThread(epoch, $"请求超过 60 秒，耗时 {stopwatch.ElapsedMilliseconds} ms");
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                FailOnMainThread(epoch, $"{exception.GetType().Name}: {exception.Message}，耗时 {stopwatch.ElapsedMilliseconds} ms");
            }
        }

        private static async Task<byte[]> DownloadAudioAsync(PhoneConfig config, string audioId)
        {
            string path = "/phone/audio/" + Uri.EscapeDataString(audioId);
            Log?.Log($"发出请求：GET {path}");
            using (var request = new HttpRequestMessage(HttpMethod.Get, config.BaseUrl + path))
            {
                request.Headers.TryAddWithoutValidation(TokenHeader, config.Token);
                using (HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
                    }

                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
        }

        private static void NotifyManualHangup()
        {
            if (!TryLoadConfig(out PhoneConfig config, out _))
            {
                return;
            }

            Log?.Log("发出请求：POST /phone/hangup");
            _ = NotifyManualHangupAsync(config);
        }

        private static async Task NotifyManualHangupAsync(PhoneConfig config)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, config.BaseUrl + "/phone/hangup"))
                {
                    request.Headers.TryAddWithoutValidation(TokenHeader, config.Token);
                    request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    using (HttpResponseMessage response = await Http.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    {
                    }
                }
            }
            catch
            {
                // 挂断通知是尽力而为；失败不能影响游戏或显示错误。
            }
        }

        private static void ShowResponse(PhoneResponse response, byte[] audioBytes)
        {
            serverWillHangUp |= response.Hangup;
            AudioClip clip = null;
            if (audioBytes != null && audioBytes.Length > 0)
            {
                try
                {
                    clip = WavDecoder.DecodePcm16Mono44100(audioBytes, "AiPhoneRemoteVoice");
                }
                catch (Exception exception)
                {
                    Log?.Error($"音频解码失败，改为纯文字：{exception.GetType().Name}: {exception.Message}");
                }
            }

            string[] choices = response.Choices ?? Array.Empty<string>();
            var options = new List<ChoiceOption>();
            Dialogue dialogue = new Dialogue().SetText(AiName, response.Text);
            for (int index = 0; index < choices.Length; index++)
            {
                string choiceText = choices[index] ?? string.Empty;
                options.Add(new ChoiceOption { ServerIndex = index, Text = choiceText });
                AddChoice(dialogue, choiceText);
            }

            if (response.AllowFreeInput)
            {
                options.Add(new ChoiceOption { IsFreeInput = true, Text = "我想说..." });
                AddChoice(dialogue, "我想说...");
            }

            ActiveChoiceDialogue = options.Count > 0 ? dialogue : null;
            activeOptions = options.ToArray();
            if (clip != null)
            {
                try
                {
                    RegisterDynamicClip(clip);
                }
                catch (Exception exception)
                {
                    Log?.Error($"音频注册失败，改为纯文字：{exception.GetType().Name}: {exception.Message}");
                    ReleaseDynamicClip(true);
                    clip = null;
                }
            }

            if (clip == null && response.Hangup)
            {
                dialogue.SetEndAction(HangUp);
            }

            Log?.Log($"显示服务端台词；选项数 {choices.Length}；自由输入={response.AllowFreeInput}；audio={clip != null}；hangup={response.Hangup}");
            isWaitingForResponse = false;
            DialoguePhoneUIManager.Instance.StartDialogue(dialogue);
            ApplyTransfer(response.Transfer);

            if (clip != null)
            {
                try
                {
                    if (!ModHelper.PlaySound2(ModId, SoundKey))
                    {
                        throw new InvalidOperationException("游戏声音通道拒绝播放");
                    }

                    dynamicClipEndsAt = Time.realtimeSinceStartup + clip.length;
                    hangUpWhenAudioStops = response.Hangup;
                }
                catch (Exception exception)
                {
                    Log?.Error($"音频播放失败，改为纯文字：{exception.GetType().Name}: {exception.Message}");
                    ReleaseDynamicClip(true);
                    if (response.Hangup)
                    {
                        dialogue.SetEndAction(HangUp);
                    }
                }
            }
        }

        private static void ApplyTransfer(int? requestedTransfer)
        {
            if (!requestedTransfer.HasValue || requestedTransfer.Value <= 0)
            {
                return;
            }

            int singleLimit = GetTransferMaxSingle();
            int dailyLimit = GetTransferMaxDaily();
            int transferredToday = PhoneStateCache.GetTransferredToday();
            int dailyRemaining = Math.Max(0, Math.Max(0, dailyLimit) - transferredToday);
            int actualAmount = CalculateTransferAmount(
                requestedTransfer.Value,
                singleLimit,
                dailyLimit,
                transferredToday);

            if (actualAmount <= 0)
            {
                Log?.Log(
                    $"电话转账未入账：请求 {requestedTransfer.Value}，单笔上限 {singleLimit}，当日剩余额度 {dailyRemaining}。");
                return;
            }

            ModHelper.AddCredit(actualAmount, true);
            PhoneStateCache.RecordTransfer(actualAmount);
            DialoguePhoneUIManager.Instance.UpdateDialogHistory(
                $"<color=#eb4034>{AiName}:</color> 转账 {actualAmount} 信用点");
            Log?.Log(
                $"电话转账已入账：请求 {requestedTransfer.Value}，实际 {actualAmount}，" +
                $"今日累计 {PhoneStateCache.GetTransferredToday()}。");
        }

        private static int CalculateTransferAmount(
            int requested,
            int singleLimit,
            int dailyLimit,
            int transferredToday)
        {
            if (requested <= 0)
            {
                return 0;
            }

            int safeSingleLimit = Math.Max(0, singleLimit);
            int safeDailyLimit = Math.Max(0, dailyLimit);
            int dailyRemaining = Math.Max(0, safeDailyLimit - Math.Max(0, transferredToday));
            return Math.Min(requested, Math.Min(safeSingleLimit, dailyRemaining));
        }

        private static int GetTransferMaxSingle()
        {
            return Math.Max(0, SingleTransferLimitSetting?.Value ?? 500);
        }

        private static int GetTransferMaxDaily()
        {
            return Math.Max(0, DailyTransferLimitSetting?.Value ?? 1000);
        }

        internal static int GetParcelMaxPerWeek()
        {
            return Math.Max(0, ParcelPerWeekLimitSetting?.Value ?? 2);
        }

        internal static int GetParcelMaxValue()
        {
            return Math.Max(0, ParcelValueLimitSetting?.Value ?? 500);
        }

        private static ChoiceState CreateChoiceState()
        {
            return new ChoiceState
            {
                Cash = PlayerStore.Instance.GetCash(),
                TransferredToday = PhoneStateCache.GetTransferredToday(),
                TransferMaxSingle = GetTransferMaxSingle(),
                TransferMaxDaily = GetTransferMaxDaily(),
                ParcelsThisWeek = ParcelStateCache.GetDeliveredThisWeek(StoreStation.GetDayCounter()),
                ParcelMaxPerWeek = GetParcelMaxPerWeek(),
                ParcelMaxValue = GetParcelMaxValue()
            };
        }

        private static void AddChoice(Dialogue dialogue, string choiceText)
        {
            GameItem choiceItem = DirectoryMaster.Item("$", true);
            Dialogue playerLine = new Dialogue().SetText("PLAYER", choiceText);
            dialogue.AddChoice(choiceItem, choiceText, string.Empty, playerLine, null, null);
        }

        private static void RegisterDynamicClip(AudioClip clip)
        {
            ReleaseDynamicClip(true);
            dynamicClip = clip;
            hangUpWhenAudioStops = false;
            LoadedMod loadedMod = ModLoader.Mods.Find(mod => mod.Manifest.ID == ModId);
            if (loadedMod == null)
            {
                throw new InvalidOperationException("找不到已加载的 ai_phone mod");
            }

            loadedMod.Sounds[SoundKey] = clip;
            AudioDirectory.RegisterModClip(ModHelper.GetSoundIdentifier(ModId, SoundKey), clip);
        }

        private static void ReleaseFinishedClip()
        {
            if (dynamicClip == null || Time.realtimeSinceStartup < dynamicClipEndsAt)
            {
                return;
            }

            bool shouldHangUp = hangUpWhenAudioStops;
            ReleaseDynamicClip(false);
            if (shouldHangUp && isActiveCall)
            {
                HangUp();
            }
        }

        private static void ReleaseDynamicClip(bool stopPlayback)
        {
            AudioClip clip = dynamicClip;
            dynamicClip = null;
            dynamicClipEndsAt = 0f;
            hangUpWhenAudioStops = false;
            if (clip == null)
            {
                return;
            }

            try
            {
                if (AudioManager.Exists)
                {
                    AudioSource source = AudioManager.Instance.audioSource2;
                    if (source != null)
                    {
                        if (stopPlayback)
                        {
                            source.Stop();
                        }

                        if (source.clip == clip)
                        {
                            source.clip = null;
                        }
                    }
                }

                LoadedMod loadedMod = ModLoader.Mods.Find(mod => mod.Manifest.ID == ModId);
                if (loadedMod != null && loadedMod.Sounds.TryGetValue(SoundKey, out AudioClip registered) && registered == clip)
                {
                    loadedMod.Sounds.Remove(SoundKey);
                }

                AudioDirectory.RegisterModClip(ModHelper.GetSoundIdentifier(ModId, SoundKey), null);
            }
            catch (Exception exception)
            {
                Log?.Error($"注销临时语音失败：{exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                UnityEngine.Object.Destroy(clip);
                Log?.Log("临时语音已注销并释放。");
            }
        }

        private static void OpenFreeInputWindow()
        {
            if (CustomUIManager.Instance == null)
            {
                Log?.Error("无法打开自由输入框：CustomUIManager 尚未初始化");
                return;
            }

            CloseFreeInputWindow();
            float scale = DialogueTextScale;
            CustomUIBuilder builder = CustomUIManager.Window(
                    FreeInputWindowId,
                    $"对 {AiName} 说",
                    CustomUIManager.LAYER_OVERLAY)
                .SetSize(440f * scale, 240f * scale)
                .Center()
                .SetDraggable(false)
                .SetCloseOnEscape(true)
                .AddInput("输入想说的话", string.Empty, _ => { }, "message")
                .BeginRow()
                .AddFlexibleSpace()
                .AddButton("发送", SubmitFreeInput)
                .AddButton("取消", CloseFreeInputWindow)
                .End();

            DialogueTextScaling.ConfigureFreeInput(
                builder.Handle.Get("message"),
                SubmitFreeInput);
            builder.Show();
        }

        private static void SubmitFreeInput()
        {
            CustomUIWindow window = CustomUIManager.Instance?.GetWindow(FreeInputWindowId);
            if (window == null)
            {
                return;
            }

            string text = window.Get("message").GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            Log?.Log("提交自由输入。");
            WritePlayerRecorderLine(text);
            DialoguePhoneUIManager.Instance.HideChoiceBubble();
            SendChoice(null, text);
        }

        private static void WritePlayerRecorderLine(string text)
        {
            DialoguePhoneUIManager.Instance.UpdateDialogHistory(
                "<color=#6ecf5f>Player:</color> " + (text ?? string.Empty));
        }

        private static void CloseFreeInputWindow()
        {
            CustomUIWindow window = CustomUIManager.Instance?.GetWindow(FreeInputWindowId);
            window?.Close();
        }

        private static void ShowWaiting()
        {
            isWaitingForResponse = true;
            Dialogue waiting = new Dialogue().SetText(AiName, "…");
            DialoguePhoneUIManager.Instance.StartDialogue(waiting);
        }

        private static void FailOnMainThread(int epoch, string reason)
        {
            MainThreadActions.Enqueue(() =>
            {
                if (!IsCurrent(epoch))
                {
                    return;
                }

                Log?.Error($"请求失败：{reason}");
                isWaitingForResponse = false;
                Dialogue failure = new Dialogue().SetText(AiName, "信号不好，没接通").SetEndAction(HangUp);
                DialoguePhoneUIManager.Instance.StartDialogue(failure);
            });
        }

        internal static bool PrepareRecorderEntry(ref string text, out bool recordInLedger)
        {
            recordInLedger = !isActiveCall;
            if (!isActiveCall)
            {
                return true;
            }

            if (isWaitingForResponse)
            {
                return false;
            }

            const string customerLabel = "<color=#eb4034>Customer :</color>";
            if (text != null && text.StartsWith(customerLabel, StringComparison.Ordinal))
            {
                text = $"<color=#eb4034>{AiName}:</color>" + text.Substring(customerLabel.Length);
            }

            return true;
        }

        internal static void RecordRecorderEntry(string text)
        {
            if (isActiveCall)
            {
                return;
            }

            RecorderLedgerCache.Record(DialogUIManager.RemoveTag(text));
        }

        private static Dictionary<string, int> ReadReputation()
        {
            var result = new Dictionary<string, int>();
            List<StoreReputation> reputations = PlayerStore.Instance.storeReputations;
            if (reputations == null)
            {
                return result;
            }

            foreach (StoreReputation reputation in reputations)
            {
                if (reputation == null || string.IsNullOrWhiteSpace(reputation.factionId))
                {
                    continue;
                }

                result[reputation.factionId] = reputation.GetReputation();
            }

            return result;
        }

        internal static bool TryLoadConfig(out PhoneConfig config, out string error)
        {
            config = null;
            error = null;
            try
            {
                string json = ModHelper.ReadModFileText(ModId, "config.json");
                if (string.IsNullOrWhiteSpace(json))
                {
                    error = "找不到 config.json";
                    return false;
                }

                config = JsonConvert.DeserializeObject<PhoneConfig>(json);
                if (config == null || string.IsNullOrWhiteSpace(config.BaseUrl))
                {
                    error = "config.json 中未配置 baseUrl";
                    return false;
                }

                if (!Uri.TryCreate(config.BaseUrl.Trim(), UriKind.Absolute, out Uri uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    error = "config.json 中的 baseUrl 不是有效的 HTTP/HTTPS 地址";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(config.Token) || config.Token == "CHANGE_ME")
                {
                    error = "config.json 中未配置 token";
                    return false;
                }

                config.BaseUrl = config.BaseUrl.Trim().TrimEnd('/');
                config.Token = config.Token.Trim();
                return true;
            }
            catch (Exception exception)
            {
                error = $"读取 config.json 失败：{exception.GetType().Name}: {exception.Message}";
                return false;
            }
        }

        private static bool IsCurrent(int epoch)
        {
            return isActiveCall && epoch == requestEpoch;
        }

        private static void HangUp()
        {
            Log?.Log("服务端要求挂断电话。");
            PhoneStateCache.CaptureRecorderBaseline();
            isActiveCall = false;
            isWaitingForResponse = false;
            serverWillHangUp = false;
            requestEpoch++;
            ActiveChoiceDialogue = null;
            activeOptions = Array.Empty<ChoiceOption>();
            CloseFreeInputWindow();
            ReleaseDynamicClip(true);
            DialoguePhoneUIManager.Instance.currentDialog = null;
            DialoguePhoneUIManager.Instance.EndConversation();
            PhoneUIManager.Instance.OnPhoneDialogEnd(true);
        }

        private sealed class ChoiceOption
        {
            public int? ServerIndex { get; set; }
            public string Text { get; set; }
            public bool IsFreeInput { get; set; }
        }

        private sealed class CallRequest
        {
            [JsonProperty("contact")]
            public string Contact { get; set; }
            [JsonProperty("number")]
            public string Number { get; set; }
            [JsonProperty("textOnly")]
            public bool TextOnly { get; set; }
            [JsonProperty("state")]
            public CallState State { get; set; }

            [JsonProperty("cashAtLastCall")]
            public int? CashAtLastCall { get; set; }

            [JsonProperty("recorder", NullValueHandling = NullValueHandling.Ignore)]
            public string[] Recorder { get; set; }

            [JsonProperty("notices", NullValueHandling = NullValueHandling.Ignore)]
            public string[] Notices { get; set; }

            [JsonProperty("calendar", NullValueHandling = NullValueHandling.Ignore)]
            public string[] Calendar { get; set; }

            [JsonProperty("nightReport", NullValueHandling = NullValueHandling.Ignore)]
            public string[] NightReport { get; set; }

            [JsonProperty("giftHints")]
            public GiftHints GiftHints { get; set; }
        }

        private sealed class CallState
        {
            [JsonProperty("cash")]
            public object Cash { get; set; }
            [JsonProperty("day", NullValueHandling = NullValueHandling.Ignore)]
            public object Day { get; set; }

            [JsonProperty("storeName", NullValueHandling = NullValueHandling.Ignore)]
            public string StoreName { get; set; }

            [JsonProperty("playerName", NullValueHandling = NullValueHandling.Ignore)]
            public string PlayerName { get; set; }

            [JsonProperty("reputation")]
            public Dictionary<string, int> Reputation { get; set; }

            [JsonProperty("salesToday", NullValueHandling = NullValueHandling.Ignore)]
            public int? SalesToday { get; set; }

            [JsonProperty("customersServedTotal", NullValueHandling = NullValueHandling.Ignore)]
            public int? CustomersServedTotal { get; set; }

            [JsonProperty("shopOpen", NullValueHandling = NullValueHandling.Ignore)]
            public bool? ShopOpen { get; set; }

            [JsonProperty("rentDueInDays", NullValueHandling = NullValueHandling.Ignore)]
            public int? RentDueInDays { get; set; }

            [JsonProperty("rentAmount", NullValueHandling = NullValueHandling.Ignore)]
            public int? RentAmount { get; set; }

            [JsonProperty("transferredToday")]
            public int TransferredToday { get; set; }

            [JsonProperty("storeState", NullValueHandling = NullValueHandling.Ignore)]
            public string StoreState { get; set; }

            [JsonProperty("transferMaxSingle")]
            public int TransferMaxSingle { get; set; }

            [JsonProperty("transferMaxDaily")]
            public int TransferMaxDaily { get; set; }

            [JsonProperty("parcelsThisWeek")]
            public int ParcelsThisWeek { get; set; }

            [JsonProperty("parcelMaxPerWeek")]
            public int ParcelMaxPerWeek { get; set; }

            [JsonProperty("parcelMaxValue")]
            public int ParcelMaxValue { get; set; }
        }

        private sealed class ChoiceRequest
        {
            [JsonProperty("index")]
            public int? Index { get; set; }
            [JsonProperty("text")]
            public string Text { get; set; }
            [JsonProperty("textOnly")]
            public bool TextOnly { get; set; }

            [JsonProperty("state")]
            public ChoiceState State { get; set; }
        }

        private sealed class ChoiceState
        {
            [JsonProperty("cash")]
            public int Cash { get; set; }

            [JsonProperty("transferredToday")]
            public int TransferredToday { get; set; }

            [JsonProperty("transferMaxSingle")]
            public int TransferMaxSingle { get; set; }

            [JsonProperty("transferMaxDaily")]
            public int TransferMaxDaily { get; set; }

            [JsonProperty("parcelsThisWeek")]
            public int ParcelsThisWeek { get; set; }

            [JsonProperty("parcelMaxPerWeek")]
            public int ParcelMaxPerWeek { get; set; }

            [JsonProperty("parcelMaxValue")]
            public int ParcelMaxValue { get; set; }
        }

        private sealed class PhoneResponse
        {
            [JsonProperty("text")]
            public string Text { get; set; }
            [JsonProperty("choices")]
            public string[] Choices { get; set; }
            [JsonProperty("allowFreeInput")]
            public bool AllowFreeInput { get; set; }
            [JsonProperty("audio")]
            public AudioDescriptor Audio { get; set; }
            [JsonProperty("hangup")]
            public bool Hangup { get; set; }

            [JsonProperty("transfer")]
            public int? Transfer { get; set; }
        }

        private sealed class AudioDescriptor
        {
            [JsonProperty("id")]
            public string Id { get; set; }
            [JsonProperty("format")]
            public string Format { get; set; }
        }
    }

    [HarmonyPatch(typeof(PhoneUIManager), nameof(PhoneUIManager.HandleCall))]
    internal static class HandleAiPhoneCallPatch
    {
        private static bool Prefix(PhoneUIManager __instance, long currentNumber)
        {
            if (currentNumber != Prototype.PhoneNumber) return true;
            Prototype.Log?.Log($"拨号：{Prototype.PhoneNumber}（{Prototype.AiName}）" );
            __instance.StartPhoneDialogSequence();
            return false;
        }
    }

    [HarmonyPatch(typeof(PhoneUIManager), nameof(PhoneUIManager.StartPhoneDialog))]
    internal static class StartAiPhoneDialoguePatch
    {
        private static bool Prefix(long currentNumber, ref bool ___isTalking)
        {
            if (currentNumber != Prototype.PhoneNumber) return true;
            ___isTalking = true;
            Prototype.BeginCall();
            return false;
        }
    }

    [HarmonyPatch(typeof(DialoguePhoneUIManager), nameof(DialoguePhoneUIManager.SelectChoice))]
    internal static class HandleAiPhoneChoicePatch
    {
        private static bool Prefix(DialoguePhoneUIManager __instance, int index)
        {
            if (!Prototype.IsActiveChoice(__instance.currentDialog)) return true;
            Prototype.Choose(index);
            return false;
        }
    }

    [HarmonyPatch(typeof(DialoguePhoneUIManager), nameof(DialoguePhoneUIManager.UpdateDialogHistory))]
    internal static class FixAiPhoneRecorderPatch
    {
        private static bool Prefix(ref string text, out bool __state)
        {
            return Prototype.PrepareRecorderEntry(ref text, out __state);
        }

        private static void Postfix(string text, bool __state)
        {
            if (__state)
            {
                Prototype.RecordRecorderEntry(text);
            }
        }
    }

    [HarmonyPatch(typeof(DialogUIManager), nameof(DialogUIManager.UpdateDialogHistory))]
    internal static class CaptureGameRecorderPatch
    {
        private static void Postfix(string text)
        {
            Prototype.RecordRecorderEntry(text);
        }
    }

    [HarmonyPatch(typeof(PhoneUIManager), "Update")]
    internal static class PhoneMainThreadPatch
    {
        private static void Postfix() => Prototype.RunMainThreadActions();
    }

    [HarmonyPatch(typeof(StartOfDayUIManager), nameof(StartOfDayUIManager.InitPanel))]
    internal static class CaptureNightReportPatch
    {
        private static void Prefix()
        {
            PhoneStateCache.CaptureNightReport();
        }
    }

    [HarmonyPatch(typeof(PlayerStore), nameof(PlayerStore.LoadGame))]
    internal static class ResetTransferLimitOnLoadPatch
    {
        private static void Postfix()
        {
            PhoneStateCache.RollBackOnLoad();
        }
    }

    [HarmonyPatch(typeof(PhoneUIManager), nameof(PhoneUIManager.StopCall))]
    internal static class CancelAiPhoneCallPatch
    {
        private static void Postfix() => Prototype.CancelActiveCall();
    }

    [HarmonyPatch(typeof(PhoneUIManager), nameof(PhoneUIManager.WillAnswerCall))]
    internal static class AlwaysAnswerAiPhonePatch
    {
        private static bool Prefix(long number, ref bool __result)
        {
            if (number != Prototype.PhoneNumber) return true;
            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(PhoneBookUIManager), nameof(PhoneBookUIManager.OpenUI))]
    internal static class AddAiPhoneQuickContactPatch
    {
        private sealed class InjectionState
        {
            public long Number { get; set; }
            public bool HadOriginal { get; set; }
            public StorePhoneClient Original { get; set; }
        }

        private static void Prefix(out InjectionState __state)
        {
            __state = new InjectionState { Number = Prototype.PhoneNumber };
            Dictionary<long, StorePhoneClient> contacts = PlayerStore.Instance.PhoneClientDict;
            if (contacts.TryGetValue(__state.Number, out StorePhoneClient original))
            {
                __state.HadOriginal = true;
                __state.Original = original;
            }

            contacts[__state.Number] = new StorePhoneClient
            {
                phoneState = StorePhoneClient.PhoneState.Regular,
                dialedBefore = true,
                locID = Prototype.AiName
            };
        }

        private static Exception Finalizer(Exception __exception, InjectionState __state)
        {
            Dictionary<long, StorePhoneClient> contacts = PlayerStore.Instance.PhoneClientDict;
            if (__state == null)
            {
                return __exception;
            }

            if (__state.HadOriginal)
            {
                contacts[__state.Number] = __state.Original;
            }
            else
            {
                contacts.Remove(__state.Number);
            }

            return __exception;
        }
    }

    [HarmonyPatch(typeof(ContactElement), nameof(ContactElement.OnInit))]
    internal static class LabelAiPhoneQuickContactPatch
    {
        private static void Postfix(ContactElement __instance, long targetNumber)
        {
            if (targetNumber != Prototype.PhoneNumber) return;
            __instance.titleTMP.text = Prototype.AiName;
            __instance.statusTMP.text = "就绪";
        }
    }
}
