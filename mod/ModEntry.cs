namespace AiPhone
{
    public sealed class ModEntry : IMod
    {
        private ModLog log;
        private bool hooksRegistered;

        public void Init(ModManifest manifest)
        {
            log = new ModLog(manifest);
            Prototype.Log = log;
            Prototype.TextOnlySetting = ModHelper.GetConfig(manifest.ID).Define(
                "text_only",
                false,
                "Phone",
                () => "纯文字模式",
                () => "开启后不请求或播放语音。",
                false);
            Prototype.TextLanguageSetting = ModHelper.GetConfig(manifest.ID).Define(
                "text_language",
                string.Empty,
                "Phone",
                () => "屏幕文字语言",
                choices: null,
                tooltip: () => "适用于电话台词和选项，例如简体中文。留空沿用电话服务设置，从下一轮生效。",
                requiresRestart: false);
            Prototype.VoiceLanguageSetting = ModHelper.GetConfig(manifest.ID).Define(
                "voice_language",
                string.Empty,
                "Phone",
                () => "语音语言",
                choices: null,
                tooltip: () => "适用于通话语音稿，例如 English。留空沿用电话服务设置，从下一轮生效。",
                requiresRestart: false);
            Prototype.SendRecorderSetting = ModHelper.GetConfig(manifest.ID).Define(
                "send_recorder",
                true,
                "Phone",
                () => "录音机内容是否发送",
                () => "开启后，来电会发送上次通话之后新增的录音机条目。",
                false);
            Prototype.SingleTransferLimitSetting = ModHelper.GetConfig(manifest.ID).Define(
                "single_transfer_limit",
                500,
                "Phone",
                () => "电话转账单笔上限",
                min: 0,
                max: null,
                tooltip: () => "每次服务端转账最多实际到账的信用点。",
                requiresRestart: false);
            Prototype.DailyTransferLimitSetting = ModHelper.GetConfig(manifest.ID).Define(
                "daily_transfer_limit",
                1000,
                "Phone",
                () => "电话转账单日上限",
                min: 0,
                max: null,
                tooltip: () => "每个游戏日通过电话最多实际到账的信用点。",
                requiresRestart: false);
            Prototype.AiNameSetting = ModHelper.GetConfig(manifest.ID).Define(
                "ai_name",
                "Assistant",
                "Phone",
                () => "AI 名字",
                choices: null,
                tooltip: () => "联系人名、电话录音机说话人以及默认便签署名。",
                requiresRestart: false);
            Prototype.PhoneNumberSetting = ModHelper.GetConfig(manifest.ID).Define(
                "phone_number",
                10000,
                "Phone",
                () => "电话号码",
                min: 1,
                max: null,
                tooltip: () => "拨打这个号码接通 AI 联系人。",
                requiresRestart: false);
            Prototype.OpeningMessageSetting = ModHelper.GetConfig(manifest.ID).Define(
                OpeningMessageInputPatch.SettingKey,
                string.Empty,
                "Phone",
                () => "开局留言",
                choices: null,
                tooltip: () => "用自己的口吻和电话伙伴打个招呼。新建电话会话时发送，留空使用默认留言。",
                requiresRestart: false);
            Prototype.SessionResetSetting = ModHelper.GetConfig(manifest.ID).Define(
                "phone_session_reset",
                string.Empty,
                "Phone",
                () => "为当前存档换新电话会话",
                choices: null,
                tooltip: () => "下次拨号或日报时，命令行模式会为这个存档开启新会话。",
                requiresRestart: false);
            Prototype.DialogueTextSizeSetting = ModHelper.GetConfig(manifest.ID).Define(
                "dialogue_text_size",
                100,
                "Display",
                () => "对话文字大小",
                min: 100,
                max: 160,
                tooltip: () => "调整 NPC、电话对话气泡和自由输入框的文字大小。",
                requiresRestart: false);
            Prototype.NoteSignatureSetting = ModHelper.GetConfig(manifest.ID).Define(
                "note_signature",
                string.Empty,
                "Parcel",
                () => "便签署名",
                choices: null,
                tooltip: () => "留空时自动跟随 AI 名字。",
                requiresRestart: false);
            Prototype.ParcelValueLimitSetting = ModHelper.GetConfig(manifest.ID).Define(
                "parcel_value_limit",
                500,
                "Parcel",
                () => "单个包裹基础价值上限",
                min: 0,
                max: null,
                tooltip: () => "超出时从请求末尾开始删除整项物品。",
                requiresRestart: false);
            Prototype.ParcelPerWeekLimitSetting = ModHelper.GetConfig(manifest.ID).Define(
                "parcel_weekly_limit",
                2,
                "Parcel",
                () => "每游戏周包裹上限",
                min: 0,
                max: null,
                tooltip: () => "第 1～7 天为第一周；只统计成功送达的包裹。",
                requiresRestart: false);
            Prototype.AllowContrabandSetting = ModHelper.GetConfig(manifest.ID).Define(
                "parcel_allow_contraband",
                false,
                "Parcel",
                () => "允许包裹包含违禁品",
                () => "关闭时，违禁品不会进入安全清单，也不会被本地生成。",
                false);
            Prototype.AllowStolenSetting = ModHelper.GetConfig(manifest.ID).Define(
                "parcel_allow_stolen",
                false,
                "Parcel",
                () => "允许包裹包含赃物",
                () => "关闭时，赃物不会进入安全清单，也不会被本地生成。",
                false);
            Prototype.RandomEventsEnabledSetting = ModHelper.GetConfig(manifest.ID).Define(
                "random_events_enabled",
                true,
                "Events",
                () => "随机事件",
                () => "允许电话里的 AI 偶尔遇到自己那边发生的事。",
                false);
            Prototype.EventChanceSetting = ModHelper.GetConfig(manifest.ID).Define(
                "random_event_chance",
                33,
                "Events",
                () => "事件概率",
                min: 0,
                max: 100,
                tooltip: () => "普通来电触发随机事件的百分比；连续剧到期集不受影响。",
                requiresRestart: false);
            Prototype.EventMinCallGapSetting = ModHelper.GetConfig(manifest.ID).Define(
                "random_event_min_call_gap",
                2,
                "Events",
                () => "两次事件最少间隔通话数",
                min: 0,
                max: null,
                tooltip: () => "事件发生后，至少经过这么多通电话才会再次随机抽取。",
                requiresRestart: false);
            Prototype.UnreachableEnabledSetting = ModHelper.GetConfig(manifest.ID).Define(
                "random_event_unreachable_enabled",
                true,
                "Events",
                () => "允许偶尔打不通",
                () => "关闭后，普通事件和连续剧都不会触发无法接通。",
                false);
            Prototype.UnreachableChanceSetting = ModHelper.GetConfig(manifest.ID).Define(
                "random_event_unreachable_chance",
                10,
                "Events",
                () => "打不通频率",
                min: 0,
                max: 100,
                tooltip: () => "普通事件触发后，改从打不通事件池抽取的百分比；连续剧到期集不受影响。",
                requiresRestart: false);
            Prototype.RentProtectionSetting = ModHelper.GetConfig(manifest.ID).Define(
                "random_event_rent_protection",
                false,
                "Events",
                () => "交租保护",
                () => "开启后，交租当天或现金不足以支付当期房租时不会触发打不通。",
                false);
            Prototype.ForcedEventSetting = ModHelper.GetConfig(manifest.ID).Define(
                "random_event_force_next",
                string.Empty,
                "Events",
                () => "调试：下一通强制事件 ID",
                choices: null,
                tooltip: () => "填写 events.txt 中的 id；同一个填写值只使用一次，并无视概率和每日限制。再次测试同一项时先清空再填回。",
                requiresRestart: false);
            RegisterHooks();
            log.Log($"Definitely Yours 已初始化；{Prototype.AiName} 的直拨号码是 {Prototype.PhoneNumber}。" );
        }

        public void OnEnable()
        {
            RegisterHooks();
            log.Log($"电话协议 v1 已启用。可直拨 {Prototype.PhoneNumber} 或点击电话簿中的 {Prototype.AiName}。" );
        }

        public void OnDisable()
        {
            UnregisterHooks();
            Prototype.CancelActiveCall();
            log.Log("Definitely Yours 已停用。");
        }

        private void RegisterHooks()
        {
            if (hooksRegistered)
            {
                return;
            }

            ModHook.OnModItemDirectoryInit += ParcelItemFactory.Register;
            ModHook.OnModItemDirectoryInit += LetterBookService.Register;
            ModHook.OnNewGameStarted += ParcelService.OnNewGameStarted;
            ModHook.OnNewGameStarted += RandomEventService.OnNewGameStarted;
            ModHook.OnGameLoadedLate += ParcelService.OnGameLoaded;
            ModHook.OnGameLoadedLate += LetterBookService.OnGameLoaded;
            ModHook.OnGameLoadedLate += RandomEventService.OnGameLoaded;
            ModHook.OnGoingSleepLate += ParcelService.OnGoingSleepLate;
            ModHook.OnShutterOpenedLate += ParcelService.OnShutterOpenedLate;
            hooksRegistered = true;
        }

        private void UnregisterHooks()
        {
            if (!hooksRegistered)
            {
                return;
            }

            ModHook.OnModItemDirectoryInit -= ParcelItemFactory.Register;
            ModHook.OnModItemDirectoryInit -= LetterBookService.Register;
            ModHook.OnNewGameStarted -= ParcelService.OnNewGameStarted;
            ModHook.OnNewGameStarted -= RandomEventService.OnNewGameStarted;
            ModHook.OnGameLoadedLate -= ParcelService.OnGameLoaded;
            ModHook.OnGameLoadedLate -= LetterBookService.OnGameLoaded;
            ModHook.OnGameLoadedLate -= RandomEventService.OnGameLoaded;
            ModHook.OnGoingSleepLate -= ParcelService.OnGoingSleepLate;
            ModHook.OnShutterOpenedLate -= ParcelService.OnShutterOpenedLate;
            hooksRegistered = false;
        }
    }
}
