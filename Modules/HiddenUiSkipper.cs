using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace EM2026PerfBoost.Modules
{
    /// <summary>
    /// Eliminates redundant hidden vanilla UI rebuilds underneath iamgri mod's replacement screens:
    /// - InboxWindow.Renew: skipped ONLY when DataEmails & UI filter state are 100% unchanged and _rows is populated.
    /// - TalkWindow.Renew / RenewLeftMessages: skipped ONLY when DataTalks state is 100% unchanged and _rows is populated.
    /// - TransfersWindow.Refresh: skipped AFTER first init once EMSkinCore.TransfersPage._root is alive.
    /// - ShopWindow_v2.Refresh / ApplyFilters: skipped once EMSkinCore.ShopPage._root is built & alive.
    /// - Basecamp_v2.Refresh / Renew: skipped once EMSkinCore.BasecampPage._root is built & alive.
    /// - UiKit.Unseen: disables GraphicRaycaster on hidden vanilla canvases so EventSystem skips invisible raycasts.
    /// </summary>
    internal static class HiddenUiSkipper
    {
        // Dirty-check state for InboxWindow
        private static long _emailMutationVersion = 1;
        private static IntPtr _lastInboxWindowPtr = IntPtr.Zero;
        private static long _lastInboxFingerprint = -1;
        private static string _lastInboxSlot = "";
        private static int _lastInboxDateKey = -1;

        // Dirty-check state for TalkWindow
        private static IntPtr _lastTalkWindowPtr = IntPtr.Zero;
        private static long _lastTalkFingerprint = -1;
        private static string _lastTalkSlot = "";
        private static int _lastTalkDateKey = -1;

        // First-init guard for TransfersWindow (so TransferListMemberRow exists for RemoveListing)
        private static IntPtr _initializedTransfersPtr = IntPtr.Zero;
        private static string _initializedTransfersSlot = "";

        public static void ResetState()
        {
            _lastInboxWindowPtr = IntPtr.Zero;
            _lastInboxFingerprint = -1;
            _lastInboxSlot = "";
            _lastInboxDateKey = -1;

            _lastTalkWindowPtr = IntPtr.Zero;
            _lastTalkFingerprint = -1;
            _lastTalkSlot = "";
            _lastTalkDateKey = -1;

            _initializedTransfersPtr = IntPtr.Zero;
            _initializedTransfersSlot = "";
            Interlocked.Increment(ref _emailMutationVersion);
        }

        public static void InstallGamePatches(Harmony harmony)
        {
            PatchEmailMutations(harmony);
            PatchInbox(harmony);
            PatchTalk(harmony);
            PatchTransfers(harmony);
            PatchShopAndBasecamp(harmony);
            PatchSquadAndStaff(harmony);
        }

        public static void AttachToCore(Harmony coreHarmony, Assembly coreAsm)
        {
            try
            {
                var uiKit = coreAsm.GetType("UiKit", false);
                if (uiKit == null) return;

                var unseen = uiKit.GetMethod("Unseen", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (unseen != null)
                {
                    coreHarmony.Patch(
                        unseen,
                        postfix: new HarmonyMethod(typeof(HiddenUiSkipper), nameof(UiKit_Unseen_Postfix)));
                    Plugin.Logger.LogMessage("[HiddenUiSkipper] Hooked EMSkinCore.UiKit.Unseen to disable hidden GraphicRaycasters.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[HiddenUiSkipper] UiKit.Unseen attach failed: {ex.Message}");
            }
        }

        private static void PatchEmailMutations(Harmony harmony)
        {
            var bump = new HarmonyMethod(typeof(HiddenUiSkipper), nameof(BumpEmailVersion));
            string[] mutators =
            {
                nameof(DataEmails.Add),
                nameof(DataEmails.Delete),
                nameof(DataEmails.DeleteAllInactive),
                nameof(DataEmails.DeleteByAuthor),
                nameof(DataEmails.MarkAsRead),
                nameof(DataEmails.MarkAllAsRead),
                nameof(DataEmails.DeactivateAllByAuthor),
                nameof(DataEmails.Deactivate),
                nameof(DataEmails.Reset)
            };

            int hooked = 0;
            foreach (var name in mutators)
            {
                try
                {
                    var m = AccessTools.Method(typeof(DataEmails), name);
                    if (m != null)
                    {
                        harmony.Patch(m, postfix: bump);
                        hooked++;
                    }
                }
                catch { }
            }

            // Also invalidate on explicit InboxWindow UI interactions
            string[] inboxMutators =
            {
                nameof(InboxWindow.ChangeSort),
                nameof(InboxWindow.OnSearchChanged),
                nameof(InboxWindow.OnPageClicked),
                nameof(InboxWindow.DeleteAll),
                nameof(InboxWindow.ReadAll)
            };
            foreach (var name in inboxMutators)
            {
                try
                {
                    var m = AccessTools.Method(typeof(InboxWindow), name);
                    if (m != null)
                        harmony.Patch(m, prefix: bump);
                }
                catch { }
            }

            Plugin.Logger.LogMessage($"[HiddenUiSkipper] Hooked {hooked} DataEmails mutation trackers.");
        }

        private static void BumpEmailVersion()
        {
            Interlocked.Increment(ref _emailMutationVersion);
        }

        private static void PatchInbox(Harmony harmony)
        {
            try
            {
                var renew = AccessTools.Method(typeof(InboxWindow), nameof(InboxWindow.Renew));
                if (renew != null)
                {
                    harmony.Patch(
                        renew,
                        prefix: new HarmonyMethod(typeof(HiddenUiSkipper), nameof(InboxWindow_Renew_Prefix)),
                        postfix: new HarmonyMethod(typeof(HiddenUiSkipper), nameof(InboxWindow_Renew_Postfix)));
                    Plugin.Logger.LogMessage("[HiddenUiSkipper] Patched InboxWindow.Renew with exact dirty-check memoization.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[HiddenUiSkipper] InboxWindow patch failed: {ex.Message}");
            }
        }

        private static void PatchTalk(Harmony harmony)
        {
            try
            {
                var renew = AccessTools.Method(typeof(TalkWindow), nameof(TalkWindow.Renew));
                if (renew != null)
                {
                    harmony.Patch(
                        renew,
                        prefix: new HarmonyMethod(typeof(HiddenUiSkipper), nameof(TalkWindow_Renew_Prefix)),
                        postfix: new HarmonyMethod(typeof(HiddenUiSkipper), nameof(TalkWindow_Renew_Postfix)));
                    Plugin.Logger.LogMessage("[HiddenUiSkipper] Patched TalkWindow.Renew with exact dirty-check memoization.");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[HiddenUiSkipper] TalkWindow patch failed: {ex.Message}");
            }
        }

        private static void PatchTransfers(Harmony harmony)
        {
            try
            {
                var refresh = AccessTools.Method(typeof(TransfersWindow), nameof(TransfersWindow.Refresh));
                if (refresh != null)
                {
                    harmony.Patch(
                        refresh,
                        prefix: new HarmonyMethod(typeof(HiddenUiSkipper), nameof(TransfersWindow_Refresh_Prefix)),
                        postfix: new HarmonyMethod(typeof(HiddenUiSkipper), nameof(TransfersWindow_Refresh_Postfix)));
                    Plugin.Logger.LogMessage("[HiddenUiSkipper] Patched TransfersWindow.Refresh (skips hidden rebuilds after first init).");
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[HiddenUiSkipper] TransfersWindow patch failed: {ex.Message}");
            }
        }

        private static void PatchShopAndBasecamp(Harmony harmony)
        {
            try
            {
                var shopRefresh = AccessTools.Method(typeof(ShopWindow_v2), nameof(ShopWindow_v2.Refresh));
                var shopFilter = AccessTools.Method(typeof(ShopWindow_v2), nameof(ShopWindow_v2.ApplyFilters));
                var shopPre = new HarmonyMethod(typeof(HiddenUiSkipper), nameof(ShopWindow_Hidden_Prefix));
                if (shopRefresh != null) harmony.Patch(shopRefresh, prefix: shopPre);
                if (shopFilter != null) harmony.Patch(shopFilter, prefix: shopPre);

                var bcRefresh = AccessTools.Method(typeof(Basecamp_v2), nameof(Basecamp_v2.Refresh));
                var bcRenew = AccessTools.Method(typeof(Basecamp_v2), nameof(Basecamp_v2.Renew));
                var bcPre = new HarmonyMethod(typeof(HiddenUiSkipper), nameof(Basecamp_Hidden_Prefix));
                if (bcRefresh != null) harmony.Patch(bcRefresh, prefix: bcPre);
                if (bcRenew != null) harmony.Patch(bcRenew, prefix: bcPre);

                Plugin.Logger.LogMessage("[HiddenUiSkipper] Patched ShopWindow_v2 and Basecamp_v2 hidden rebuilds.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[HiddenUiSkipper] Shop/Basecamp patch failed: {ex.Message}");
            }
        }

        private static void PatchSquadAndStaff(Harmony harmony)
        {
            try
            {
                var squadRenew = AccessTools.Method(typeof(SquadWindow), nameof(SquadWindow.Renew));
                if (squadRenew != null)
                    harmony.Patch(squadRenew, prefix: new HarmonyMethod(typeof(HiddenUiSkipper), nameof(SquadWindow_Renew_Prefix)));

                var staffRenew = AccessTools.Method(typeof(SquadStaffWindow), nameof(SquadStaffWindow.Renew));
                if (staffRenew != null)
                    harmony.Patch(staffRenew, prefix: new HarmonyMethod(typeof(HiddenUiSkipper), nameof(SquadStaffWindow_Renew_Prefix)));

                Plugin.Logger.LogMessage("[HiddenUiSkipper] Patched SquadWindow.Renew and SquadStaffWindow.Renew hidden rebuilds.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogWarning($"[HiddenUiSkipper] Squad/Staff patch failed: {ex.Message}");
            }
        }

        private static long ComputeInboxFingerprint(InboxWindow win)
        {
            unchecked
            {
                long h = 17;
                h = h * 31 + Interlocked.Read(ref _emailMutationVersion);

                var emails = DataEmails.Emails;
                int count = emails != null ? emails.Count : 0;
                h = h * 31 + count;
                h = h * 31 + DataEmails.GetUnreadCount();

                if (emails != null && count > 0)
                {
                    // Hash every email's pointer + IsRead + IsActive (fast O(N) over ~20-150 items, ~0.02 ms)
                    for (int i = 0; i < count; i++)
                    {
                        var e = emails[i];
                        if (e == null) continue;
                        h = h * 31 + e.Pointer.ToInt64();
                        h = h * 31 + (e.IsRead ? 1 : 0) + (e.IsActive ? 2 : 0);
                    }
                }

                if (win != null)
                {
                    h = h * 31 + win._page;
                    h = h * 31 + (win._sortType?.GetHashCode() ?? 0);
                }

                return h;
            }
        }

        private static bool InboxWindow_Renew_Prefix(InboxWindow __instance)
        {
            try
            {
                if (!Plugin.CfgSkipHiddenVanillaUi.Value || __instance == null)
                    return true;

                // Only skip when iamgri mod's InboxPage is active and built
                if (!CoreBridge.IsCorePageRootAlive("InboxPage"))
                    return true;

                var rows = __instance._rows;
                if (rows == null || rows.Count == 0)
                    return true;

                string slot = SlotController.CurrentSlot ?? "";
                int dateKey = CurrentDateKey();
                long fp = ComputeInboxFingerprint(__instance);

                if (__instance.Pointer == _lastInboxWindowPtr
                    && fp == _lastInboxFingerprint
                    && dateKey == _lastInboxDateKey
                    && string.Equals(slot, _lastInboxSlot, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref PerfTelemetry.InboxRenewsSkipped);
                    return false;
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        private static void InboxWindow_Renew_Postfix(InboxWindow __instance)
        {
            try
            {
                if (__instance == null) return;
                _lastInboxWindowPtr = __instance.Pointer;
                _lastInboxSlot = SlotController.CurrentSlot ?? "";
                _lastInboxDateKey = CurrentDateKey();
                _lastInboxFingerprint = ComputeInboxFingerprint(__instance);
                Interlocked.Increment(ref PerfTelemetry.InboxRenewsExecuted);
            }
            catch { }
        }

        private static long ComputeTalkFingerprint()
        {
            unchecked
            {
                long h = 23;
                var talks = DataTalks.Talks;
                int count = talks != null ? talks.Count : 0;
                h = h * 31 + count;
                h = h * 31 + DataTalks.ReturnCountUnreadMessages();

                if (talks != null && count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        var t = talks[i];
                        if (t == null) continue;
                        h = h * 31 + t.Pointer.ToInt64();
                        var msgs = t.Messages;
                        h = h * 31 + (msgs != null ? msgs.Count : 0);
                        h = h * 31 + (t.IsEnd ? 1 : 0);
                    }
                }
                return h;
            }
        }

        private static bool TalkWindow_Renew_Prefix(TalkWindow __instance)
        {
            try
            {
                if (!Plugin.CfgSkipHiddenVanillaUi.Value || __instance == null)
                    return true;

                if (!CoreBridge.IsCorePageRootAlive("TalkPage"))
                    return true;

                var rows = __instance._rows;
                if (rows == null || rows.Count == 0)
                    return true;

                string slot = SlotController.CurrentSlot ?? "";
                int dateKey = CurrentDateKey();
                long fp = ComputeTalkFingerprint();

                if (__instance.Pointer == _lastTalkWindowPtr
                    && fp == _lastTalkFingerprint
                    && dateKey == _lastTalkDateKey
                    && string.Equals(slot, _lastTalkSlot, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref PerfTelemetry.TalkRenewsSkipped);
                    return false;
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        private static void TalkWindow_Renew_Postfix(TalkWindow __instance)
        {
            try
            {
                if (__instance == null) return;
                _lastTalkWindowPtr = __instance.Pointer;
                _lastTalkSlot = SlotController.CurrentSlot ?? "";
                _lastTalkDateKey = CurrentDateKey();
                _lastTalkFingerprint = ComputeTalkFingerprint();
                Interlocked.Increment(ref PerfTelemetry.TalkRenewsExecuted);
            }
            catch { }
        }

        private static bool TransfersWindow_Refresh_Prefix(TransfersWindow __instance)
        {
            try
            {
                if (!Plugin.CfgSkipHiddenVanillaUi.Value || __instance == null)
                    return true;

                if (!CoreBridge.IsCorePageRootAlive("TransfersPage"))
                    return true;

                string slot = SlotController.CurrentSlot ?? "";
                if (__instance.Pointer == _initializedTransfersPtr
                    && string.Equals(slot, _initializedTransfersSlot, StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref PerfTelemetry.TransfersHiddenSkipped);
                    return false;
                }

                return true;
            }
            catch
            {
                return true;
            }
        }

        private static void TransfersWindow_Refresh_Postfix(TransfersWindow __instance)
        {
            try
            {
                if (__instance == null) return;
                _initializedTransfersPtr = __instance.Pointer;
                _initializedTransfersSlot = SlotController.CurrentSlot ?? "";
            }
            catch { }
        }

        private static bool ShopWindow_Hidden_Prefix()
        {
            try
            {
                if (!Plugin.CfgSkipHiddenVanillaUi.Value)
                    return true;

                if (CoreBridge.IsCorePageRootAlive("ShopPage"))
                {
                    Interlocked.Increment(ref PerfTelemetry.ShopHiddenSkipped);
                    return false;
                }
            }
            catch { }
            return true;
        }

        private static bool Basecamp_Hidden_Prefix()
        {
            try
            {
                if (!Plugin.CfgSkipHiddenVanillaUi.Value)
                    return true;

                if (CoreBridge.IsCorePageRootAlive("BasecampPage"))
                {
                    Interlocked.Increment(ref PerfTelemetry.BasecampHiddenSkipped);
                    return false;
                }
            }
            catch { }
            return true;
        }

        private static bool SquadWindow_Renew_Prefix(SquadWindow __instance)
        {
            try
            {
                if (!Plugin.CfgSkipHiddenVanillaUi.Value || __instance == null)
                    return true;

                // SquadTable only reads SquadWindow._team (set by Init before Renew); skip hidden card rebuilds
                if (__instance._team != null && CoreBridge.IsCorePageRootAlive("SquadTable"))
                {
                    Interlocked.Increment(ref PerfTelemetry.SquadHiddenSkipped);
                    return false;
                }
            }
            catch { }
            return true;
        }

        private static bool SquadStaffWindow_Renew_Prefix()
        {
            try
            {
                if (!Plugin.CfgSkipHiddenVanillaUi.Value)
                    return true;

                // StaffTable accesses zero fields of SquadStaffWindow once built
                if (CoreBridge.IsCorePageRootAlive("StaffTable"))
                {
                    Interlocked.Increment(ref PerfTelemetry.StaffHiddenSkipped);
                    return false;
                }
            }
            catch { }
            return true;
        }

        private static void UiKit_Unseen_Postfix(Transform __0)
        {
            try
            {
                if (!Plugin.CfgDisableHiddenRaycasters.Value || __0 == null)
                    return;

                var canvas = __0.GetComponent<Canvas>();
                if (canvas == null || canvas.enabled)
                    return;

                var raycaster = __0.GetComponent<GraphicRaycaster>();
                if (raycaster != null && raycaster.enabled)
                {
                    raycaster.enabled = false;
                    Interlocked.Increment(ref PerfTelemetry.RaycastersDisabled);
                }

                // Also disable LayoutGroups & ContentSizeFitters inside completely hidden vanilla subtrees
                // so Unity doesn't recalculate layouts on every SetActive(true) tab switch
                var layouts = __0.GetComponentsInChildren<LayoutGroup>(true);
                if (layouts != null)
                {
                    for (int i = 0; i < layouts.Length; i++)
                    {
                        var lg = layouts[i];
                        if (lg != null && lg.enabled)
                        {
                            lg.enabled = false;
                            Interlocked.Increment(ref PerfTelemetry.HiddenLayoutsDisabled);
                        }
                    }
                }

                var fitters = __0.GetComponentsInChildren<ContentSizeFitter>(true);
                if (fitters != null)
                {
                    for (int i = 0; i < fitters.Length; i++)
                    {
                        var f = fitters[i];
                        if (f != null && f.enabled)
                        {
                            f.enabled = false;
                            Interlocked.Increment(ref PerfTelemetry.HiddenLayoutsDisabled);
                        }
                    }
                }
            }
            catch { }
        }

        private static int CurrentDateKey()
        {
            try
            {
                var d = GlobalValues.CurrentDate;
                return d.Year * 1000 + d.DayOfYear;
            }
            catch
            {
                return -1;
            }
        }
    }
}
