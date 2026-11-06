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
            RegisterHooks();
            log.Log($"Definitely Yours 已初始化；{Prototype.AiName} 的直拨号码是 {Prototype.PhoneNumber}。" );
        }

        public void OnEnable()
        {
            RegisterHooks();
            log.Log($"协议 v1.7.1 已启用。可直拨 {Prototype.PhoneNumber} 或点击电话簿中的 {Prototype.AiName}。" );
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
            ModHook.OnGameLoadedLate += ParcelService.OnGameLoaded;
            ModHook.OnGameLoadedLate += LetterBookService.OnGameLoaded;
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
            ModHook.OnGameLoadedLate -= ParcelService.OnGameLoaded;
            ModHook.OnGameLoadedLate -= LetterBookService.OnGameLoaded;
            ModHook.OnGoingSleepLate -= ParcelService.OnGoingSleepLate;
            ModHook.OnShutterOpenedLate -= ParcelService.OnShutterOpenedLate;
            hooksRegistered = false;
        }
    }
}
