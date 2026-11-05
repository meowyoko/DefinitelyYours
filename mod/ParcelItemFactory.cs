using System;
using System.Collections.Generic;
using System.Linq;

namespace AiPhone
{
    internal static class ParcelItemFactory
    {
        internal const string ParcelItemId = "ai_phone.parcel";
        private const string StyleTag = "AI_PHONE_PARCEL_STYLE";
        private const string ModId = "ai_phone";

        private static readonly HashSet<GameItem> PendingDestroy = new HashSet<GameItem>();
        private static readonly HashSet<GameItem> DestroyedParcels = new HashSet<GameItem>();
        private static ModItemDirectory directory;

        internal static void Register(ModItemDirectory modDirectory)
        {
            if (modDirectory == null || ReferenceEquals(directory, modDirectory))
            {
                return;
            }

            directory = modDirectory;
            bool added = directory.Add(ParcelItemId, CreateDefinition);
            Prototype.Log?.Log(added
                ? "已注册包裹物品 ai_phone.parcel（内部 7×5，五种样式均支持开合贴图）。"
                : "包裹物品 ai_phone.parcel 已存在，跳过重复注册。" );
        }

        internal static GameItem CreateParcel(
            string style,
            IList<ParcelOrderItem> items,
            ParcelNote note,
            IList<string> rejected)
        {
            GameItem parcel = DirectoryMaster.Item(ParcelItemId, true);
            if (parcel == null)
            {
                return null;
            }

            string normalizedStyle = ParcelService.NormalizeStyle(style);
            SetStyle(parcel, normalizedStyle);
            ApplyParcelSprite(parcel, isOpen: false);
            parcel.name = $"{Prototype.AiName} 的包裹";
            parcel.shortDescription = DescriptionForStyle(normalizedStyle);

            GameInventory inventory = parcel.contentWindow?.child as GameInventory;
            if (inventory == null)
            {
                DestroyTree(parcel);
                return null;
            }

            int receivedDay = StoreStation.GetDayCounter();
            string originalTitle = note?.Title?.Trim();
            string receivedTitle = string.IsNullOrWhiteSpace(originalTitle)
                ? $"第{receivedDay}天"
                : $"第{receivedDay}天 · {originalTitle}";
            GameItem handnote = PreBuiltItemHelper.CreateHandnote(
                note?.Body ?? string.Empty,
                receivedTitle,
                Prototype.NoteSignature,
                0);
            handnote.name = $"{Prototype.AiName} 的字条";
            handnote.shortDescription = $"夹在包裹里的，是 {Prototype.AiName} 的字迹。";
            LetterBookService.MarkParcelNote(handnote);
            inventory.TryAcceptAll(handnote);
            if (handnote.parentInventory != inventory)
            {
                handnote.Destroy();
                DestroyTree(parcel);
                return null;
            }

            foreach (ParcelOrderItem line in items ?? Array.Empty<ParcelOrderItem>())
            {
                if (!TryAddCompleteLine(inventory, line))
                {
                    rejected?.Add(line?.Id ?? string.Empty);
                }
            }

            return parcel;
        }

        internal static void DestroyTree(GameItem parcel)
        {
            if (parcel == null || !DestroyedParcels.Add(parcel))
            {
                return;
            }

            try
            {
                GameInventory inventory = parcel.contentWindow?.child as GameInventory;
                if (inventory != null)
                {
                    foreach (GameItem child in inventory.childItems.ToArray())
                    {
                        child?.Destroy();
                    }
                }

                parcel.Destroy();
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"销毁未投递包裹失败：{exception.GetType().Name}: {exception.Message}" );
            }
        }

        private static GameItem CreateDefinition()
        {
            var created = DirectoryUtils.CreateInventoryWindow(7, 5, true);
            GameItem parcel = directory.Empty()
                .SetContentWindow(created.Item1)
                .SetSpriteAndShape("Items/items_container", "eng_box");
            parcel.name = $"{Prototype.AiName} 的包裹";
            parcel.shortDescription = DescriptionForStyle("plain");
            parcel.unitValue = 0;
            ContainerHelper.InitContainerItemIncludeContainer(created.Item2, parcel);
            ModHelper.SetSource(parcel, ModId, "Definitely Yours");
            SetStyle(parcel, "plain");
            AttachSpriteState(parcel, created.Item1);
            AttachEmptyCleanup(parcel, created.Item2);
            return parcel;
        }

        private static void AttachSpriteState(GameItem parcel, PixelWindow window)
        {
            Action oldOpen = window.openCallback;
            Action oldClose = window.closeCallback;
            window.openCallback = () =>
            {
                oldOpen?.Invoke();
                ApplyParcelSprite(parcel, isOpen: true);
            };
            window.closeCallback = () =>
            {
                oldClose?.Invoke();
                ApplyParcelSprite(parcel, isOpen: false);
            };
        }

        private static void SetStyle(GameItem parcel, string style)
        {
            parcel.ModifyTag(StyleTag, tag => tag.SetString(style), false);
        }

        private static string GetStyle(GameItem parcel)
        {
            try
            {
                return ParcelService.NormalizeStyle(parcel.GetTagReadonly(StyleTag).GetString());
            }
            catch
            {
                return "plain";
            }
        }

        private static void ApplyParcelSprite(GameItem parcel, bool isOpen)
        {
            string style = GetStyle(parcel);
            string key = isOpen ? $"parcel_{style}_open" : $"parcel_{style}";
            LoadedMod loadedMod = ModLoader.Mods.Find(mod => mod.Manifest.ID == ModId);
            if (loadedMod != null && loadedMod.Sprites.ContainsKey(key))
            {
                parcel.SetSpriteAndShapeFromMod(ModId, key);
            }
            else
            {
                parcel.SetSpriteAndShape("Items/items_container", isOpen ? "eng_box_u" : "eng_box");
            }

            // 开合两张图的透明区不同，不能让引擎重新计算形状，否则开箱时会挤动背包。
            int width = style == "courier" || style == "cute" ? 3 : 4;
            parcel.SetShape(new GridShapeBuilder().SetData(
                Enumerable.Repeat((byte)1, width * 3).ToArray(),
                width));
        }

        private static string DescriptionForStyle(string style)
        {
            switch (style)
            {
                case "gift":
                    return "系着缎带的礼盒，看得出挑过。";
                case "courier":
                    return "太空站快递的硬壳箱，搭扣扣得严严实实。";
                case "crate":
                    return "钉好的木箱，缝里漏出点干草。";
                case "cute":
                    return $"一个粉粉的小盒子，有点不像 {Prototype.AiName} 会挑的。";
                default:
                    return "一个普通的纸箱，胶带缠得有点认真过头。";
            }
        }

        private static void AttachEmptyCleanup(GameItem parcel, GameInventory inventory)
        {
            inventory.onSlotRemoveItemLateFunc += (_, __, ___) => ScheduleEmptyCheck(parcel, inventory);
            inventory.onSlotChangeAmountItemFunc += (_, __, ___) => ScheduleEmptyCheck(parcel, inventory);
        }

        private static void ScheduleEmptyCheck(GameItem parcel, GameInventory inventory)
        {
            if (parcel == null || inventory == null || DestroyedParcels.Contains(parcel)
                || !PendingDestroy.Add(parcel))
            {
                return;
            }

            Prototype.EnqueueNextFrame(() =>
            {
                PendingDestroy.Remove(parcel);
                if (DestroyedParcels.Contains(parcel))
                {
                    return;
                }

                if (inventory.childItems.Count != 0)
                {
                    return;
                }

                Prototype.Log?.Log("包裹已被拿空，自动销毁空箱。" );
                DestroyedParcels.Add(parcel);
                parcel.Destroy();
            });
        }

        private static bool TryAddCompleteLine(GameInventory inventory, ParcelOrderItem line)
        {
            if (line == null || line.Amount <= 0 || string.IsNullOrWhiteSpace(line.Id))
            {
                return false;
            }

            Dictionary<GameItem, int> baseline = inventory.childItems.ToDictionary(
                item => item,
                item => Math.Max(1, item.unitCount));
            int remaining = line.Amount;

            while (remaining > 0)
            {
                GameItem item = null;
                try
                {
                    item = DirectoryMaster.Item(line.Id, true);
                    if (!ParcelService.IsSafeParcelInstance(line.Id, item, out _))
                    {
                        item?.Destroy();
                        RollBackLine(inventory, baseline);
                        return false;
                    }

                    SlotMarker slot = inventory.TryFindOneValidInventorySlot(item);
                    int maximum = item.MaxNumAdd(slot);
                    if (slot == null || maximum <= 0)
                    {
                        item.Destroy();
                        RollBackLine(inventory, baseline);
                        return false;
                    }

                    int amount = Math.Min(remaining, maximum);
                    int before = CountAmount(inventory, line.Id);
                    item.SetAmount(amount);
                    inventory.TryAcceptAll(item);
                    int accepted = CountAmount(inventory, line.Id) - before;
                    if (accepted != amount)
                    {
                        if (item.parentInventory == null)
                        {
                            item.Destroy();
                        }

                        RollBackLine(inventory, baseline);
                        return false;
                    }

                    remaining -= amount;
                }
                catch
                {
                    if (item != null && item.parentInventory == null)
                    {
                        item.Destroy();
                    }

                    RollBackLine(inventory, baseline);
                    return false;
                }
            }

            return true;
        }

        private static int CountAmount(GameInventory inventory, string id)
        {
            int count = 0;
            foreach (GameItem item in inventory.childItems)
            {
                if (item != null && string.Equals(item.identifier, id, StringComparison.Ordinal))
                {
                    count += Math.Max(1, item.unitCount);
                }
            }

            return count;
        }

        private static void RollBackLine(GameInventory inventory, Dictionary<GameItem, int> baseline)
        {
            foreach (GameItem item in inventory.childItems.ToArray())
            {
                if (!baseline.TryGetValue(item, out int oldAmount))
                {
                    item.Destroy();
                    continue;
                }

                if (Math.Max(1, item.unitCount) != oldAmount)
                {
                    item.SetAmount(oldAmount);
                }
            }
        }
    }
}
