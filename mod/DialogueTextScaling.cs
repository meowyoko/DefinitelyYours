using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace AiPhone
{
    internal static class DialogueTextScaling
    {
        private const float FreeInputHeight = 90f;
        private static readonly Dictionary<TextBubble, BubbleLayout> BubbleLayouts =
            new Dictionary<TextBubble, BubbleLayout>();

        internal static void Apply(TextBubble bubble)
        {
            if (bubble == null || bubble.textfield == null)
            {
                return;
            }

            if (!BubbleLayouts.TryGetValue(bubble, out BubbleLayout layout))
            {
                layout = BubbleLayout.Capture(bubble);
                BubbleLayouts[bubble] = layout;
            }

            TextMeshProUGUI text = bubble.textfield;
            text.fontSize = layout.FontSize * Prototype.DialogueTextScale;
            text.enableWordWrapping = true;
            text.overflowMode = TextOverflowModes.Overflow;
            text.ForceMeshUpdate();

            float preferredHeight = text.GetPreferredValues(
                text.text,
                layout.TextWidth,
                0f).y;
            float padding = 12f * Prototype.DialogueTextScale;
            float availableHeight = Mathf.Max(
                layout.TextHeight,
                layout.BackgroundHeight - padding);
            float targetBubbleHeight = layout.BackgroundHeight;
            if (preferredHeight > availableHeight)
            {
                targetBubbleHeight = Mathf.Min(
                    preferredHeight + padding,
                    layout.BackgroundHeight * 1.2f);
            }

            float extraHeight = Mathf.Max(0f, targetBubbleHeight - layout.BackgroundHeight);
            float targetTextHeight = Mathf.Max(
                layout.TextHeight,
                Mathf.Min(preferredHeight, targetBubbleHeight - padding));

            SetHeight(text.rectTransform, targetTextHeight);
            SetHeight(layout.Background, targetBubbleHeight);
            if (layout.Root != layout.Background)
            {
                SetHeight(layout.Root, layout.RootHeight + extraHeight);
            }
        }

        internal static void ConfigureFreeInput(CustomUIElement element, Action submit)
        {
            if (element?.input == null)
            {
                return;
            }

            float scale = Prototype.DialogueTextScale;
            element.SetPreferredSize(0f, FreeInputHeight * scale);

            TMP_InputField input = element.input;
            input.lineType = TMP_InputField.LineType.MultiLineSubmit;
            input.textComponent.fontSize *= scale;
            input.textComponent.enableWordWrapping = true;
            input.textComponent.overflowMode = TextOverflowModes.Overflow;
            input.textComponent.alignment = TextAlignmentOptions.TopLeft;

            if (input.placeholder is TMP_Text placeholder)
            {
                placeholder.fontSize *= scale;
                placeholder.enableWordWrapping = true;
                placeholder.alignment = TextAlignmentOptions.TopLeft;
            }

            input.onSubmit.AddListener(_ => submit());
        }

        private static void SetHeight(RectTransform rect, float height)
        {
            if (rect != null && height > 0f)
            {
                rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
            }
        }

        private sealed class BubbleLayout
        {
            internal float FontSize;
            internal float TextWidth;
            internal float TextHeight;
            internal RectTransform Background;
            internal float BackgroundHeight;
            internal RectTransform Root;
            internal float RootHeight;

            internal static BubbleLayout Capture(TextBubble bubble)
            {
                RectTransform textRect = bubble.textfield.rectTransform;
                RectTransform background = bubble.backgroundImage?.rectTransform;
                RectTransform root = bubble.transform as RectTransform;
                return new BubbleLayout
                {
                    FontSize = bubble.textfield.fontSize,
                    TextWidth = textRect.rect.width,
                    TextHeight = textRect.rect.height,
                    Background = background,
                    BackgroundHeight = background?.rect.height ?? 0f,
                    Root = root,
                    RootHeight = root?.rect.height ?? 0f
                };
            }
        }
    }

    [HarmonyPatch(typeof(DialogUIManager), nameof(DialogUIManager.DisplayClientText))]
    internal static class ScaleNpcClientTextPatch
    {
        private static void Postfix(DialogUIManager __instance) =>
            DialogueTextScaling.Apply(__instance.clientBubble);
    }

    [HarmonyPatch(typeof(DialogUIManager), nameof(DialogUIManager.DisplayerPlayerText))]
    internal static class ScaleNpcPlayerTextPatch
    {
        private static void Postfix(DialogUIManager __instance) =>
            DialogueTextScaling.Apply(__instance.playerBubble);
    }

    [HarmonyPatch(typeof(DialogUIManager), nameof(DialogUIManager.DisplaySecText))]
    internal static class ScaleNpcSecTextPatch
    {
        private static void Postfix(DialogUIManager __instance) =>
            DialogueTextScaling.Apply(__instance.secBubble);
    }

    [HarmonyPatch(typeof(DialoguePhoneUIManager), nameof(DialoguePhoneUIManager.DisplayClientText))]
    internal static class ScalePhoneClientTextPatch
    {
        private static void Postfix(DialoguePhoneUIManager __instance) =>
            DialogueTextScaling.Apply(__instance.clientBubble);
    }

    [HarmonyPatch(typeof(DialoguePhoneUIManager), nameof(DialoguePhoneUIManager.DisplayerPlayerText))]
    internal static class ScalePhonePlayerTextPatch
    {
        private static void Postfix(DialoguePhoneUIManager __instance) =>
            DialogueTextScaling.Apply(__instance.playerBubble);
    }
}
