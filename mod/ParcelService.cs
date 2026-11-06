using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace AiPhone
{
    internal static class ParcelService
    {
        private const string TokenHeader = "X-Phone-Token";
        private static readonly HashSet<string> Styles = new HashSet<string>(StringComparer.Ordinal)
        {
            "plain", "gift", "courier", "crate", "cute"
        };

        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };

        private static readonly Dictionary<string, string[]> ProfessionItems =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["water_merchant"] = new[]
                {
                    "bottled_water", "small_bottled_water", "large_bottled_water", "water_ration",
                    "water_filter", "water_filter_adv", "water_tablet", "water_test_strip",
                    "hydroreed_filter", "uv_filter"
                },
                ["hydroponic_farmer"] = new[]
                {
                    "nutrient_tablet", "bottled_water", "water_filter", "hydroreed_filter", "kotton_fabric"
                },
                ["gunsmith"] = new[] { "nuts_metal", "plastic_bag" },
                ["chemist"] = new[]
                {
                    "100ml_cylinder", "50ml_cylinder", "25ml_cylinder", "5ml_dropper", "1ml_dropper",
                    "bottled_water", "kotton_fabric"
                },
                ["rancher"] = new[] { "bottled_water", "water_ration", "nutrient_tablet" },
                ["aquafarmer"] = new[]
                {
                    "bottled_water", "large_bottled_water", "nutrient_tablet", "hydroreed_filter", "water_filter"
                },
                ["moonshiner"] = new[] { "empty_beer_bottle", "mini_bottle", "bottled_water" },
                ["street_food_vendor"] = new[]
                {
                    "processed_meat", "processed_cheese", "cup_noodle", "paper_towel", "bottled_water"
                },
                ["junker"] = new[] { "nuts_metal", "plastic_bag" }
            };

        private static Dictionary<string, CatalogItem> safeCatalog =
            new Dictionary<string, CatalogItem>(StringComparer.Ordinal);
        private static Task loadSyncTask = Task.CompletedTask;
        private static Task lastDailyTask = Task.CompletedTask;

        internal static void OnGameLoaded()
        {
            try
            {
                int day = StoreStation.GetDayCounter();
                ParcelStateCache.OnGameLoaded(day);
                CatalogRequest catalog = BuildSafeCatalog(day);
                if (!Prototype.TryLoadConfig(out PhoneConfig config, out string error))
                {
                    Prototype.Log?.Error($"包裹读档同步未发送：{error}" );
                    return;
                }

                loadSyncTask = SendLoadSyncAsync(config, day, catalog);
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"初始化包裹协议失败：{exception.GetType().Name}: {exception.Message}" );
            }
        }

        internal static void OnGoingSleepLate()
        {
            try
            {
                if (!ParcelStateCache.TryMarkDailySent())
                {
                    return;
                }

                if (!Prototype.TryLoadConfig(out PhoneConfig config, out string error))
                {
                    Prototype.Log?.Error($"每日状态未发送：{error}" );
                    return;
                }

                object body = Prototype.CreateDailyRequest();
                PhoneStateCache.CaptureRecorderBaseline();
                lastDailyTask = PostNoResponseAsync(config, "/phone/daily", body);
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"准备每日状态失败：{exception.GetType().Name}: {exception.Message}" );
            }
        }

        internal static void OnShutterOpenedLate()
        {
            try
            {
                int day = StoreStation.GetDayCounter();
                if (!ParcelStateCache.TryMarkPickupAttempt(day))
                {
                    return;
                }

                if (!Prototype.TryLoadConfig(out PhoneConfig config, out string error))
                {
                    Prototype.Log?.Error($"包裹领取未发送：{error}" );
                    return;
                }

                _ = PickupAsync(config, day);
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"准备包裹领取失败：{exception.GetType().Name}: {exception.Message}" );
            }
        }

        internal static GiftHints BuildGiftHints()
        {
            string profession = ModHelper.GetStartingScenarioId() ?? string.Empty;
            var hints = new GiftHints { Profession = profession };
            Dictionary<string, int> inventoryCounts = ReadInventoryCounts();
            Dictionary<string, int> score = new Dictionary<string, int>(StringComparer.Ordinal);

            if (ProfessionItems.TryGetValue(profession, out string[] professionItems))
            {
                foreach (string id in professionItems)
                {
                    if (!safeCatalog.TryGetValue(id, out CatalogItem catalogItem))
                    {
                        continue;
                    }

                    score[id] = 20;
                    inventoryCounts.TryGetValue(id, out int count);
                    if (count < 3)
                    {
                        hints.LowStock.Add(new GiftHintItem
                        {
                            Id = id,
                            Name = catalogItem.Name,
                            Count = count
                        });
                        score[id] += 100;
                    }
                }
            }

            IEnumerable<IGrouping<string, SoldItem>> soldGroups =
                (PlayerStore.Instance.soldItemsTracker ?? new List<SoldItem>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.itemId))
                .GroupBy(item => item.itemId, StringComparer.Ordinal);
            foreach (IGrouping<string, SoldItem> group in soldGroups)
            {
                if (!safeCatalog.TryGetValue(group.Key, out CatalogItem catalogItem))
                {
                    continue;
                }

                int count = group.Count();
                hints.SoldToday.Add(new GiftHintItem
                {
                    Id = group.Key,
                    Name = catalogItem.Name,
                    Count = count
                });
                score.TryGetValue(group.Key, out int current);
                score[group.Key] = current + Math.Min(50, count * 10);
            }

            hints.Suggested = score
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(30)
                .Select(pair => pair.Key)
                .ToList();
            return hints;
        }

        internal static bool IsSafeParcelInstance(string id, GameItem item, out string reason)
        {
            if (item == null || string.IsNullOrWhiteSpace(id) || !safeCatalog.ContainsKey(id))
            {
                reason = "不在本地安全清单";
                return false;
            }

            return IsSafeDefinition(id, item, null, out reason);
        }

        internal static string NormalizeStyle(string style)
        {
            string normalized = (style ?? string.Empty).Trim().ToLowerInvariant();
            if (Styles.Contains(normalized))
            {
                return normalized;
            }

            Prototype.Log?.Error($"未知包裹样式 '{style ?? "null"}'，回退为 plain。" );
            return "plain";
        }

        private static CatalogRequest BuildSafeCatalog(int day)
        {
            var items = new Dictionary<string, CatalogItem>(StringComparer.Ordinal);
            int errors = 0;
            foreach (string directoryName in DirectoryMaster.GetDictionaryClassNameList<GameItem>())
            {
                if (!directoryName.EndsWith("ItemDirectory", StringComparison.Ordinal)
                    || IsExcludedDirectory(directoryName))
                {
                    continue;
                }

                List<string> identifiers = DirectoryMaster.GetIdentifierList<GameItem>(directoryName);
                if (identifiers == null)
                {
                    continue;
                }

                foreach (string id in identifiers)
                {
                    GameItem item = null;
                    try
                    {
                        item = DirectoryMaster.Item(id, true);
                        if (!IsSafeDefinition(id, item, directoryName, out _))
                        {
                            continue;
                        }

                        long value = item.GetBaseValue();
                        if (value <= 0)
                        {
                            continue;
                        }

                        items[id] = new CatalogItem
                        {
                            Id = id,
                            Name = item.name ?? id,
                            Types = item.GetGameItemType()?.ToArray() ?? Array.Empty<string>(),
                            BaseValue = (int)Math.Min(int.MaxValue, value),
                            Contraband = ContrabandHelper.IsContraband(item),
                            Stolen = StolenHelper.IsStolenItem(item),
                            GridWidth = Math.Max(1, (item.modifiedShape ?? item.shape)?.width ?? 1),
                            GridHeight = Math.Max(1, (item.modifiedShape ?? item.shape)?.height ?? 1),
                            GridCells = Math.Max(1, item.GetModifiedItemSize())
                        };
                    }
                    catch
                    {
                        errors++;
                    }
                    finally
                    {
                        item?.Destroy();
                    }
                }
            }

            safeCatalog = items;
            Prototype.Log?.Log($"本地安全清单已建立：{items.Count} 项；跳过异常定义 {errors} 项。" );
            return new CatalogRequest { Day = day, Items = items.Values.OrderBy(item => item.Id).ToList() };
        }

        private static bool IsSafeDefinition(string id, GameItem item, string directoryName, out string reason)
        {
            reason = null;
            if (item == null || string.IsNullOrWhiteSpace(id)
                || id.IndexOf("save_bag", StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf("unused", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(id, ParcelItemFactory.ParcelItemId, StringComparison.Ordinal))
            {
                reason = "内部或未使用物品";
                return false;
            }

            if (!string.IsNullOrEmpty(directoryName)
                && directoryName.IndexOf("Unused", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                reason = "未使用目录";
                return false;
            }

            if (item.GetBaseValue() <= 0)
            {
                reason = "基础价值为零";
                return false;
            }

            if (item.IsTag(GeneralHelper.IMPORTANT_TAG)
                || UniqueItemHelper.IsHaveUniqueId(item)
                || item.IsGameItemType(TypeHelper.NOT_FOR_RESALE)
                || item.IsGameItemType(TypeHelper.DOCUMENT)
                || item.IsTag(PermitHelper.PERMIT_ITEM_TAG)
                || item.IsTag(GeneralHelper.LOCKED_TAG)
                || LockHelper.IsLockedContainer(item)
                || GeneralHelper.IsItemLocked(item)
                || GeneralHelper.IsDisplayOnly(item)
                || item.IsGameItemType(TypeHelper.MACHINE)
                || item.IsGameItemType(TypeHelper.SUPPLY_CRATE)
                || item.IsGameItemType(TypeHelper.STORAGE)
                || item.contentWindow != null
                || item.IsTag(ContainerHelper.CONTAINER_TAG))
            {
                reason = "受保护、容器、机器或不可转售物品";
                return false;
            }

            if (!(Prototype.AllowContrabandSetting?.Value ?? false) && ContrabandHelper.IsContraband(item))
            {
                reason = "违禁品开关关闭";
                return false;
            }

            if (!(Prototype.AllowStolenSetting?.Value ?? false) && StolenHelper.IsStolenItem(item))
            {
                reason = "赃物开关关闭";
                return false;
            }

            return true;
        }

        private static bool IsExcludedDirectory(string name)
        {
            return name.IndexOf("Unused", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Container", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Furniture", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Ship", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Character", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Ability", StringComparison.OrdinalIgnoreCase) >= 0
                || string.Equals(name, "ModItemDirectory", StringComparison.Ordinal);
        }

        private static Dictionary<string, int> ReadInventoryCounts()
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (GameItem item in PlayerStore.Instance.FindAllItem(true))
            {
                if (item == null || string.IsNullOrWhiteSpace(item.identifier))
                {
                    continue;
                }

                result.TryGetValue(item.identifier, out int count);
                result[item.identifier] = count + Math.Max(1, item.unitCount);
            }

            return result;
        }

        private static async Task SendLoadSyncAsync(PhoneConfig config, int day, CatalogRequest catalog)
        {
            await PostNoResponseAsync(config, "/phone/session", new SessionRequest { Day = day }).ConfigureAwait(false);
            await PostNoResponseAsync(config, "/phone/catalog", catalog).ConfigureAwait(false);
        }

        private static async Task PickupAsync(PhoneConfig config, int day)
        {
            await loadSyncTask.ConfigureAwait(false);
            await lastDailyTask.ConfigureAwait(false);
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                string json = JsonConvert.SerializeObject(new ParcelPickupRequest { Day = day });
                Prototype.Log?.Log($"发出请求：POST /phone/parcel/pickup；day={day}" );
                using (var request = CreateRequest(config, "/phone/parcel/pickup", json))
                using (HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false))
                {
                    string responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"HTTP {(int)response.StatusCode}" );
                    }

                    ParcelPickupResponse result = JsonConvert.DeserializeObject<ParcelPickupResponse>(responseJson);
                    Prototype.Log?.Log($"收到响应：POST /phone/parcel/pickup，耗时 {stopwatch.ElapsedMilliseconds} ms；parcel={(result?.Parcel == null ? "null" : result.Parcel.Id)}" );
                    if (result?.Parcel != null)
                    {
                        Prototype.EnqueueMainThread(() => HandleParcel(day, config, result.Parcel));
                    }
                }
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"包裹领取失败：{exception.GetType().Name}: {exception.Message}，耗时 {stopwatch.ElapsedMilliseconds} ms" );
            }
        }

        private static void HandleParcel(int day, PhoneConfig config, ParcelOrder order)
        {
            Dictionary<string, int> requested = CountRequested(order.Items);
            var packed = new Dictionary<string, int>(StringComparer.Ordinal);
            int maxPerWeek = Prototype.GetParcelMaxPerWeek();
            if (!ParcelStateCache.CanDeliverThisWeek(day, maxPerWeek))
            {
                SendResult(config, order.Id, "failed", requested, packed);
                Prototype.Log?.Log($"包裹 {order.Id} 被频率上限拒收：本周上限 {maxPerWeek}。" );
                return;
            }

            List<ParcelOrderItem> accepted = ValidateAndTrim(order.Items);
            GameItem parcel = ParcelItemFactory.CreateParcel(order.Style, accepted, order.Note, packed);
            if (parcel == null)
            {
                SendResult(config, order.Id, "failed", requested, new Dictionary<string, int>());
                Prototype.Log?.Error($"包裹 {order.Id} 创建失败。" );
                return;
            }

            string placed = TryPlaceItem(parcel);
            if (placed == "failed")
            {
                ParcelItemFactory.DestroyTree(parcel);
                packed.Clear();
            }
            else
            {
                ParcelStateCache.RecordDelivery(day, order.Id);
            }

            SendResult(config, order.Id, placed, requested, packed);
            int rejectedCount = requested.Count(pair => !packed.TryGetValue(pair.Key, out int delivered)
                || delivered < pair.Value);
            Prototype.Log?.Log($"包裹 {order.Id} 处理完成：placed={placed}；rejected={rejectedCount}。" );
        }

        private static List<ParcelOrderItem> ValidateAndTrim(IList<ParcelOrderItem> requested)
        {
            var accepted = new List<ParcelOrderItem>();
            var actualBaseValues = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ParcelOrderItem line in requested ?? Array.Empty<ParcelOrderItem>())
            {
                if (line == null || line.Amount <= 0 || string.IsNullOrWhiteSpace(line.Id)
                    || !safeCatalog.TryGetValue(line.Id, out CatalogItem catalogItem))
                {
                    continue;
                }

                GameItem probe = null;
                try
                {
                    probe = DirectoryMaster.Item(line.Id, true);
                    if (!IsSafeParcelInstance(line.Id, probe, out string reason))
                    {
                        Prototype.Log?.Log($"包裹物品 {line.Id} 被本地校验拒绝：{reason}。" );
                        continue;
                    }

                    int instanceBaseValue = (int)Math.Min(int.MaxValue, Math.Max(0L, probe.GetBaseValue()));
                    actualBaseValues.TryGetValue(line.Id, out int previousBaseValue);
                    actualBaseValues[line.Id] = Math.Max(previousBaseValue, instanceBaseValue);
                }
                catch
                {
                    continue;
                }
                finally
                {
                    probe?.Destroy();
                }

                accepted.Add(new ParcelOrderItem { Id = line.Id, Amount = line.Amount });
            }

            long total = 0;
            foreach (ParcelOrderItem line in accepted)
            {
                int baseValue = Math.Max(safeCatalog[line.Id].BaseValue, actualBaseValues[line.Id]);
                total = SaturatingAdd(total, (long)baseValue * line.Amount);
            }

            long maximum = Prototype.GetParcelMaxValue();
            while (accepted.Count > 0 && total > maximum)
            {
                int last = accepted.Count - 1;
                ParcelOrderItem trimmed = accepted[last];
                int baseValue = Math.Max(safeCatalog[trimmed.Id].BaseValue, actualBaseValues[trimmed.Id]);
                long excess = total - maximum;
                int removeAmount = (int)Math.Min(
                    trimmed.Amount,
                    Math.Max(1L, (excess + baseValue - 1L) / baseValue));
                trimmed.Amount -= removeAmount;
                total = Math.Max(0, total - (long)baseValue * removeAmount);
                Prototype.Log?.Log($"包裹价值限制：{trimmed.Id} 减少 {removeAmount} 件。" );
                if (trimmed.Amount <= 0)
                {
                    accepted.RemoveAt(last);
                }
            }

            return accepted;
        }

        private static Dictionary<string, int> CountRequested(IList<ParcelOrderItem> lines)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ParcelOrderItem line in lines ?? Array.Empty<ParcelOrderItem>())
            {
                if (line == null || line.Amount <= 0 || string.IsNullOrWhiteSpace(line.Id))
                {
                    continue;
                }

                result.TryGetValue(line.Id, out int previous);
                long total = (long)previous + line.Amount;
                result[line.Id] = (int)Math.Min(int.MaxValue, total);
            }

            return result;
        }

        private static long SaturatingAdd(long current, long addition)
        {
            if (addition <= 0)
            {
                return current;
            }

            return current > long.MaxValue - addition ? long.MaxValue : current + addition;
        }

        internal static string TryPlaceItem(GameItem item)
        {
            ModHelper.MoveItemToFrontCounter(item);
            if (item.parentInventory == EmporiumEntry.Instance.frontInvinvElement)
            {
                return "front";
            }

            ModHelper.MoveItemToBackCounter(item);
            if (item.parentInventory == EmporiumEntry.Instance.backInvinvElement)
            {
                return "back";
            }

            ModHelper.MoveItemToInventory(item);
            return item.parentInventory == EmporiumEntry.Instance.invElement ? "inventory" : "failed";
        }

        private static void SendResult(
            PhoneConfig config,
            string parcelId,
            string placed,
            IReadOnlyDictionary<string, int> requested,
            IReadOnlyDictionary<string, int> delivered)
        {
            List<ParcelItemResult> items = requested
                .Select(pair =>
                {
                    delivered.TryGetValue(pair.Key, out int deliveredAmount);
                    deliveredAmount = Math.Max(0, Math.Min(pair.Value, deliveredAmount));
                    return new ParcelItemResult
                    {
                        Id = pair.Key,
                        Requested = pair.Value,
                        Delivered = deliveredAmount,
                        Rejected = pair.Value - deliveredAmount
                    };
                })
                .ToList();
            var body = new ParcelResultRequest
            {
                Id = parcelId ?? string.Empty,
                Placed = placed,
                Rejected = items.Where(item => item.Rejected > 0).Select(item => item.Id).ToList(),
                Items = items
            };
            string summary = string.Join("；", items.Select(item =>
                $"{item.Id} 请求{item.Requested}、装入{item.Delivered}、未送达{item.Rejected}"));
            Prototype.Log?.Log($"包裹 {body.Id} 送达明细：{summary}。" );
            _ = PostNoResponseAsync(config, "/phone/parcel/result", body);
        }

        private static async Task PostNoResponseAsync(PhoneConfig config, string path, object body)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                string json = JsonConvert.SerializeObject(body);
                Prototype.Log?.Log($"发出请求：POST {path}" );
                using (var request = CreateRequest(config, path, json))
                using (HttpResponseMessage response = await Http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"HTTP {(int)response.StatusCode}" );
                    }

                    Prototype.Log?.Log($"收到响应：POST {path}，耗时 {stopwatch.ElapsedMilliseconds} ms" );
                }
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"请求失败：POST {path}；{exception.GetType().Name}: {exception.Message}，耗时 {stopwatch.ElapsedMilliseconds} ms" );
            }
        }

        private static HttpRequestMessage CreateRequest(PhoneConfig config, string path, string json)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, config.BaseUrl + path);
            request.Headers.TryAddWithoutValidation(TokenHeader, config.Token);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return request;
        }
    }
}
