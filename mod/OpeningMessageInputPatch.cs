using System;
using System.Net;
using System.Net.Http;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using TMPro;

namespace AiPhone
{
    [HarmonyPatch(typeof(ModConfigWindow), "BuildEntry")]
    internal static class OpeningMessageInputPatch
    {
        internal const string SettingKey = "phone_opening_message";
        private const string ResetWindowId = "ai_phone_new_session";
        private static readonly HttpClient StatusClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        private static void Postfix(CustomUIBuilder __0, ConfigEntry __2)
        {
            if (ReferenceEquals(__2, Prototype.SessionResetSetting))
            {
                CustomUIElement entry = __0.Last;
                entry.SetVisible(false);
                __0.AddButton("为当前存档开启新电话会话", () => ConfirmReset(entry.input));
                __0.AddButton("检查电话连接", () => CheckConnection(__0.Handle.Get("ai_phone_connection_status")),
                    "ai_phone_connection_status");
                return;
            }
            if (!ReferenceEquals(__2, Prototype.OpeningMessageSetting))
            {
                return;
            }

            CustomUIElement element = __0.Last;
            TMP_InputField input = element?.input;
            if (input == null)
            {
                return;
            }

            element.SetPreferredSize(0f, 96f);
            input.lineType = TMP_InputField.LineType.MultiLineNewline;
            input.lineLimit = 0;
            input.textComponent.enableWordWrapping = true;
            input.textComponent.alignment = TextAlignmentOptions.TopLeft;
            input.SetTextWithoutNotify(Prototype.OpeningMessageSetting.Value);
            __0.AddButton("编辑电话人设", () => OpenPersona(__0.Handle.Get("ai_phone_edit_persona")),
                "ai_phone_edit_persona");
            __0.AddLabel("保存人设后，点击「为当前存档开启新电话会话」使改动生效。");
        }

        private static async void OpenPersona(CustomUIElement element)
        {
            if (element == null || !element.IsAlive)
                return;
            if (!Prototype.TryLoadConfig(out PhoneConfig config, out _))
            {
                element.SetText("请先启动本机电话服务");
                return;
            }

            element.SetInteractable(false);
            element.SetText("正在打开人设文件……");
            string status;
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, config.BaseUrl + "/phone/persona/open"))
                {
                    request.Headers.TryAddWithoutValidation("X-Phone-Token", config.Token);
                    using (HttpResponseMessage result = await StatusClient.SendAsync(request))
                    {
                        status = result.IsSuccessStatusCode ? "编辑电话人设"
                            : result.StatusCode == HttpStatusCode.NotFound ? "请使用新版启动器编辑本机人设"
                            : "无法打开人设 · 点击重试";
                    }
                }
            }
            catch (Exception)
            {
                status = "无法打开人设 · 检查电话连接";
            }
            if (element.IsAlive)
            {
                element.SetText(status);
                element.SetInteractable(true);
            }
        }

        private static void ConfirmReset(TMP_InputField input)
        {
            CustomUIManager.Instance?.GetWindow(ResetWindowId)?.Close();
            CustomUIBuilder dialog = CustomUIManager.Window(ResetWindowId, "开启新电话会话", CustomUIManager.LAYER_OVERLAY)
                .SetSize(440f, 210f)
                .Center()
                .SetCloseOnEscape(true);
            bool hasSave = PlayerStore.Instance != null && PhoneSaveContext.HasStableSaveId();
            if (hasSave)
            {
                string save = PhoneSaveContext.CurrentSaveId();
                dialog.AddLabel("命令行模式将在下一通电话或日报时为当前存档新建会话。旧会话仍可在原客户端查看。")
                    .AddButton("确认开启", () =>
                    {
                        if (PlayerStore.Instance != null && PhoneSaveContext.CurrentSaveId() == save)
                        {
                            string token = save + "|" + Guid.NewGuid().ToString("N");
                            Prototype.SessionResetSetting.Set(token);
                            if (input != null)
                                input.SetTextWithoutNotify(token);
                            ModHelper.GetConfig("ai_phone").Save();
                        }
                        dialog.GetWindow().Close();
                    });
            }
            else
            {
                dialog.AddLabel("请先打开一个游戏存档。");
            }
            dialog.AddButton("关闭", () => dialog.GetWindow().Close()).Show();
        }

        private static async void CheckConnection(CustomUIElement element)
        {
            if (element == null || !element.IsAlive)
                return;
            if (!Prototype.TryLoadConfig(out PhoneConfig config, out _))
            {
                element.SetText("连接未配置 · 点击重试");
                return;
            }
            element.SetInteractable(false);
            element.SetText("正在检查电话服务……");
            string status;
            try
            {
                string result = await StatusClient.GetStringAsync(config.BaseUrl + "/phone/health");
                status = (bool?)JObject.Parse(result)["ok"] == true ? "电话服务在线 · 点击刷新" : "电话服务未就绪 · 点击重试";
            }
            catch (Exception)
            {
                status = "电话服务无法连接 · 点击重试";
            }
            if (element.IsAlive)
            {
                element.SetText(status);
                element.SetInteractable(true);
            }
        }
    }
}
