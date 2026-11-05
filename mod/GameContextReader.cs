using System;
using System.Collections.Generic;

namespace AiPhone
{
    internal static class GameContextReader
    {
        internal static bool? ReadShopOpen()
        {
            try
            {
                return PlayerStore.Instance.storeState == PlayerStore.StoreState.OPEN;
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"读取营业状态失败：{exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        internal static string ReadStoreState()
        {
            try
            {
                return PlayerStore.Instance.storeState.ToString();
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"读取店铺状态失败：{exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        internal static int? ReadRentDueInDays()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                return store.IsPayingRent() ? store.GetRentDayCounter() : null;
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"读取交租日期失败：{exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        internal static int? ReadRentAmount()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                return store.IsPayingRent() ? store.GetRentValue() : null;
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"读取租金失败：{exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        internal static string[] ReadNotices()
        {
            try
            {
                PlayerStore store = PlayerStore.Instance;
                var notices = new List<string>();
                if (store.IsPropertyPaid)
                {
                    return notices.ToArray();
                }

                string dueDisplay = AdvCalendarUIManager.GetFormattedDayDisplay(store.dayUntilRent);
                if (store.isPayingMortgage)
                {
                    notices.Add(LocHelper.GetLocalizedMechanic(
                        "mech_rent_loan_shark",
                        new object[] { store.mortgageWeeklyPayment, dueDisplay }));
                }
                else if (store.temporaryRent == 0)
                {
                    notices.Add(LocHelper.GetLocalizedMechanic(
                        "mech_rent_landlord",
                        new object[] { store.rentValue, dueDisplay }));
                }
                else
                {
                    notices.Add(LocHelper.GetLocalizedMechanic(
                        "mech_rent_landlord_missed",
                        new object[] { store.rentValue, store.temporaryRent, dueDisplay }));
                }

                notices.Add(LocHelper.GetLocalizedMechanic(
                    "mech_rent_property_value",
                    new object[] { store.GetStoreBuyPrice(false) }));
                return notices.ToArray();
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"读取状态提示失败：{exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        internal static string[] ReadCalendar()
        {
            try
            {
                int today = StoreStation.GetDayCounter();
                StoreEventManager manager = StoreStation.Instance.storeEventManager;
                var result = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                PlayerStore store = PlayerStore.Instance;
                int rentDueInDays = store.GetRentDayCounter();
                int rentDay = today + rentDueInDays;

                for (int day = today; day < today + 7; day++)
                {
                    List<StoreEvent> events = manager.GetActiveEventOnDay(
                        day,
                        excludeCosmetic: true,
                        excludeUnknown: true);
                    bool hasRentDay = false;
                    if (events != null)
                    {
                        foreach (StoreEvent storeEvent in events)
                        {
                            if (storeEvent == null)
                            {
                                continue;
                            }

                            hasRentDay |= storeEvent.identifier == "rent_day";
                            string key = $"{storeEvent.identifier}|{storeEvent.startDay}";
                            if (!seen.Add(key))
                            {
                                continue;
                            }

                            AddCalendarLine(result, day, storeEvent.GetDisplayName());
                        }
                    }

                    if (store.IsPayingRent() && day == rentDay && !hasRentDay)
                    {
                        StoreEvent rentEvent = EventListInnate.CreateRentDayEvent();
                        AddCalendarLine(result, day, rentEvent.GetDisplayName());
                    }
                }

                return result.ToArray();
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"读取日历失败：{exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        internal static string[] ReadNightReport()
        {
            try
            {
                return PhoneStateCache.GetNightReport();
            }
            catch (Exception exception)
            {
                Prototype.Log?.Error($"读取夜间报告失败：{exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        private static void AddCalendarLine(List<string> result, int day, string eventName)
        {
            if (string.IsNullOrWhiteSpace(eventName))
            {
                return;
            }

            string dayLabel = LocHelper.GetLocalizedMechanic("mech_day_label", new object[] { day });
            result.Add($"{dayLabel}：{eventName}");
        }
    }
}
