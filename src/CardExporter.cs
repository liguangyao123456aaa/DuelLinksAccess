using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using MelonLoader;
using Il2CppYgomGame.Card;
using Il2CppYgomGame.Deck;
using Il2CppYgomGame.Single;
using Il2CppYgomGame.Utility;
using Il2CppYgomSystem.Utility;

namespace DuelLinksAccess
{
    /// <summary>
    /// Safely exports card databases, user decks, and game recipes to formatted plain text files.
    /// Threading model:
    /// 1. Il2Cpp data extraction runs exclusively on the Unity main thread (guarantees 0 crash).
    /// 2. Windows SaveFileDialog runs in an isolated external subprocess (100% memory-isolated,
    ///    no P/Invoke stack corruption, completely accessible with NVDA/Zhengdu).
    /// 3. Remembers the user's chosen folder for all future exports.
    /// </summary>
    public static class CardExporter
    {
        private static bool _isExporting = false;
        private static readonly string ConfigDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "UserData");
        private static readonly string ConfigFilePath = Path.Combine(ConfigDir, "DuelLinksAccess_export_dir.txt");

        #region Public Hotkey Entrypoints

        /// <summary>
        /// [Ctrl+E / F8] Exports all cards currently owned in player's trunk.
        /// </summary>
        public static void ExportOwnedCards()
        {
            MelonLogger.Msg("[CardExporter] ExportOwnedCards hotkey triggered!");
            if (_isExporting)
            {
                ScreenReader.Say("导出正在进行中，请稍候...");
                return;
            }

            var content = Content.Instance;
            if (content == null)
            {
                ScreenReader.Say("卡片数据库尚未就绪，请进入游戏主菜单后再试。");
                return;
            }

            _isExporting = true;
            try
            {
                string exportText;
                int totalDistinctCount;
                int totalCopies;

                bool extracted = TryExtractCardsMainThread(out exportText, out totalDistinctCount, out totalCopies, onlyOwned: true);
                if (!extracted || totalDistinctCount == 0)
                {
                    ScreenReader.Say("未检测到您当前持有的卡片数据，请先进入卡箱或卡组编辑。");
                    _isExporting = false;
                    return;
                }

                ScreenReader.Say("正在打开保存文件窗口，请选择保存位置...");
                var ctx = new ExportContext
                {
                    Text = exportText,
                    Distinct = totalDistinctCount,
                    Copies = totalCopies,
                    DefaultFileName = $"游戏王决斗链接_我的卡片收藏_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                    DialogTitle = "请选择【我的卡片收藏】导出保存位置",
                    SuccessMessage = $"导出完成！共导出您拥有的 {totalDistinctCount} 种卡片，总计 {totalCopies} 张。"
                };

                StartWorkerThread(ctx);
            }
            catch (Exception exMain)
            {
                MelonLogger.Msg($"[CardExporter] ExportOwnedCards error: {exMain.Message}");
                ScreenReader.Say($"卡片提取失败：{exMain.Message}");
                _isExporting = false;
            }
        }

        /// <summary>
        /// [Ctrl+Shift+E] Exports master database of ALL cards implemented in the game (12,000+ cards).
        /// </summary>
        public static void ExportMasterCards()
        {
            MelonLogger.Msg("[CardExporter] ExportMasterCards hotkey triggered!");
            if (_isExporting)
            {
                ScreenReader.Say("导出正在进行中，请稍候...");
                return;
            }

            var content = Content.Instance;
            if (content == null)
            {
                ScreenReader.Say("卡片数据库尚未就绪，请进入游戏主菜单后再试。");
                return;
            }

            _isExporting = true;
            ScreenReader.Say("正在提取全游戏官方卡池总数据库，卡片数量较多，请稍候...");

            try
            {
                string exportText;
                int totalDistinctCount;
                int totalCopies;

                bool extracted = TryExtractCardsMainThread(out exportText, out totalDistinctCount, out totalCopies, onlyOwned: false);
                if (!extracted || totalDistinctCount == 0)
                {
                    ScreenReader.Say("全卡池数据提取失败，请重试。");
                    _isExporting = false;
                    return;
                }

                ScreenReader.Say("提取完成，正在打开保存文件窗口...");
                var ctx = new ExportContext
                {
                    Text = exportText,
                    Distinct = totalDistinctCount,
                    Copies = totalCopies,
                    DefaultFileName = $"游戏王决斗链接_全卡池官方总图鉴_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                    DialogTitle = "请选择【全卡池官方总图鉴】导出保存位置",
                    SuccessMessage = $"全卡池总库导出完成！共收录官方底层 {totalDistinctCount} 种卡片及完整效果。"
                };

                StartWorkerThread(ctx);
            }
            catch (Exception exMain)
            {
                MelonLogger.Msg($"[CardExporter] ExportMasterCards error: {exMain.Message}");
                ScreenReader.Say($"全卡池提取失败：{exMain.Message}");
                _isExporting = false;
            }
        }

        /// <summary>
        /// [Ctrl+Shift+D] Exports ALL user-saved decks across all characters and deck slots.
        /// </summary>
        public static void ExportAllUserDecks()
        {
            MelonLogger.Msg("[CardExporter] ExportAllUserDecks hotkey triggered!");
            if (_isExporting)
            {
                ScreenReader.Say("导出正在进行中，请稍候...");
                return;
            }

            var content = Content.Instance;
            if (content == null)
            {
                ScreenReader.Say("卡片数据库尚未就绪，请进入游戏主菜单后再试。");
                return;
            }

            _isExporting = true;
            ScreenReader.Say("正在扫描您的所有角色卡组，请稍候...");

            try
            {
                string exportText;
                int deckCount;
                bool extracted = TryExtractAllUserDecksMainThread(out exportText, out deckCount);
                if (!extracted || deckCount == 0)
                {
                    ScreenReader.Say("未能读取到已保存的卡组数据，请先进入一次卡组编辑界面。");
                    _isExporting = false;
                    return;
                }

                ScreenReader.Say($"扫描到 {deckCount} 套卡组，正在打开保存文件窗口...");
                var ctx = new ExportContext
                {
                    Text = exportText,
                    Distinct = deckCount,
                    Copies = 0,
                    DefaultFileName = $"游戏王决斗链接_我的全部角色卡组_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                    DialogTitle = "请选择【我的全部角色卡组】导出保存位置",
                    SuccessMessage = $"我的卡组导出完成！共导出您已保存的 {deckCount} 套卡组构筑。"
                };

                StartWorkerThread(ctx);
            }
            catch (Exception exMain)
            {
                MelonLogger.Msg($"[CardExporter] ExportAllUserDecks error: {exMain.Message}");
                ScreenReader.Say($"用户卡组提取失败：{exMain.Message}");
                _isExporting = false;
            }
        }

        /// <summary>
        /// [Ctrl+Shift+T] Exports all official structure decks, rental decks, starter decks, and recipes.
        /// </summary>
        public static void ExportAllGameRecipes()
        {
            MelonLogger.Msg("[CardExporter] ExportAllGameRecipes hotkey triggered!");
            if (_isExporting)
            {
                ScreenReader.Say("导出正在进行中，请稍候...");
                return;
            }

            var content = Content.Instance;
            if (content == null)
            {
                ScreenReader.Say("卡片数据库尚未就绪，请进入游戏主菜单后再试。");
                return;
            }

            _isExporting = true;
            ScreenReader.Say("正在提取游戏官方预组与全角色预设卡组，请稍候...");

            try
            {
                string exportText;
                int recipeCount;
                bool extracted = TryExtractAllGameRecipesMainThread(out exportText, out recipeCount);
                if (!extracted || recipeCount == 0)
                {
                    ScreenReader.Say("未能读取到官方预组与推荐卡组数据。");
                    _isExporting = false;
                    return;
                }

                ScreenReader.Say($"提取到 {recipeCount} 套官方卡组配方，正在打开保存文件窗口...");
                var ctx = new ExportContext
                {
                    Text = exportText,
                    Distinct = recipeCount,
                    Copies = 0,
                    DefaultFileName = $"游戏王决斗链接_官方预组与推荐卡组总览_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                    DialogTitle = "请选择【官方预组与推荐卡组】导出保存位置",
                    SuccessMessage = $"官方预组导出完成！共收录官方底层 {recipeCount} 套卡组配方。"
                };

                StartWorkerThread(ctx);
            }
            catch (Exception exMain)
            {
                MelonLogger.Msg($"[CardExporter] ExportAllGameRecipes error: {exMain.Message}");
                ScreenReader.Say($"官方配方提取失败：{exMain.Message}");
                _isExporting = false;
            }
        }

        #endregion

        #region Main Thread Extraction Logic

        private static bool TryExtractCardsMainThread(out string exportText, out int totalDistinctCount, out int totalCopies, bool onlyOwned)
        {
            exportText = "";
            totalDistinctCount = 0;
            totalCopies = 0;

            var content = Content.Instance;
            if (content == null) return false;

            TrunkData trunk = null;
            try { trunk = TrunkData.Create(); } catch { }

            var candidateMrks = new HashSet<int>();

            // Collect known card IDs
            try
            {
                var allList = content.allCardList;
                if (allList != null && allList.Count > 0)
                {
                    for (int i = 0; i < allList.Count; i++) candidateMrks.Add(allList[i]);
                }
            }
            catch { }

            if (candidateMrks.Count == 0)
            {
                try
                {
                    var mrkDict = content.mrk2idx;
                    if (mrkDict != null)
                    {
                        foreach (var key in mrkDict.Keys) candidateMrks.Add(key);
                    }
                }
                catch { }
            }

            if (candidateMrks.Count == 0)
            {
                for (int i = 1; i <= 35000; i++) candidateMrks.Add(i);
            }

            var monsterCards = new List<CardRecord>();
            var spellCards = new List<CardRecord>();
            var trapCards = new List<CardRecord>();

            foreach (int mrk in candidateMrks)
            {
                if (mrk <= 0) continue;

                int count = 0;
                if (trunk != null)
                {
                    try { count = trunk.GetNum(mrk); } catch { }
                }

                if (count <= 0)
                {
                    try { count = CardTraderInfoBase.CardPoss(mrk, 0); } catch { }
                }

                if (onlyOwned && count <= 0) continue;

                string name = "";
                try { name = content.GetName(mrk); } catch { }
                if (string.IsNullOrWhiteSpace(name) || name.Contains("未知卡片"))
                {
                    if (!onlyOwned) continue; // Skip unused slot in full dump
                    name = $"(未知卡片 #{mrk})";
                }

                totalCopies += count;

                var kind = Content.Kind.Normal;
                try { kind = content.GetKind(mrk); } catch { }

                string desc = "";
                try { desc = content.GetDesc(mrk); } catch { }
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    desc = desc.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
                }

                var record = new CardRecord
                {
                    Mrk = mrk,
                    Name = name,
                    Count = count,
                    Kind = kind,
                    Desc = desc
                };

                if (CardFormatter.IsMonsterKind(kind))
                {
                    try
                    {
                        var attr = content.GetAttr(mrk);
                        var type = content.GetType(mrk);
                        record.AttributeText = content.GetAttributeText(attr);
                        record.TypeText = content.GetTypeText(type);
                        record.KindText = content.GetKindText(kind);
                        record.Level = content.GetLevel(mrk);
                        record.Rank = content.GetRank(mrk);
                        record.LinkNum = content.GetLinkNum(mrk);
                        record.Atk = content.GetAtk(mrk);
                        record.Def = content.GetDef2(mrk);
                    }
                    catch { }
                    monsterCards.Add(record);
                }
                else if (kind == Content.Kind.Magic)
                {
                    try
                    {
                        var icon = content.GetIcon(mrk);
                        record.KindText = icon != Content.Icon.Null
                            ? $"{content.GetIconText(icon)} 魔法卡"
                            : "通常 魔法卡";
                    }
                    catch
                    {
                        record.KindText = "通常 魔法卡";
                    }
                    spellCards.Add(record);
                }
                else
                {
                    try
                    {
                        var icon = content.GetIcon(mrk);
                        record.KindText = icon != Content.Icon.Null
                            ? $"{content.GetIconText(icon)} 陷阱卡"
                            : "通常 陷阱卡";
                    }
                    catch
                    {
                        record.KindText = "通常 陷阱卡";
                    }
                    trapCards.Add(record);
                }
            }

            totalDistinctCount = monsterCards.Count + spellCards.Count + trapCards.Count;
            if (totalDistinctCount == 0) return false;

            monsterCards.Sort((a, b) => a.Mrk.CompareTo(b.Mrk));
            spellCards.Sort((a, b) => a.Mrk.CompareTo(b.Mrk));
            trapCards.Sort((a, b) => a.Mrk.CompareTo(b.Mrk));

            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            if (onlyOwned)
            {
                sb.AppendLine("                   游戏王：决斗链接 - 持有卡片及具体效果清单");
            }
            else
            {
                sb.AppendLine("                   游戏王：决斗链接 - 官方底层全卡池图鉴总库");
            }
            sb.AppendLine("================================================================================");
            sb.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"卡片种类总数：{totalDistinctCount} 种");
            if (onlyOwned)
            {
                sb.AppendLine($"持有卡片总张数：{totalCopies} 张");
            }
            sb.AppendLine($"  - 怪兽卡：{monsterCards.Count} 种");
            sb.AppendLine($"  - 魔法卡：{spellCards.Count} 种");
            sb.AppendLine($"  - 陷阱卡：{trapCards.Count} 种");
            sb.AppendLine("================================================================================");
            sb.AppendLine();

            // 1. 怪兽卡
            sb.AppendLine("################################################################################");
            sb.AppendLine($"【怪兽卡一览】(共 {monsterCards.Count} 种)");
            sb.AppendLine("################################################################################");
            sb.AppendLine();

            for (int i = 0; i < monsterCards.Count; i++)
            {
                var c = monsterCards[i];
                sb.AppendLine($"[{i + 1}] {c.Name} (卡片编号: {c.Mrk})");
                sb.AppendLine(onlyOwned ? $"    持有数量：{c.Count} 张" : $"    当前持有：{(c.Count > 0 ? $"{c.Count} 张" : "未持有")}");

                string starText = "";
                if (c.LinkNum > 0) starText = $"Link-{c.LinkNum}";
                else if (c.Rank > 0) starText = $"{c.Rank} 阶";
                else starText = $"{c.Level} 星";

                string statText = c.LinkNum > 0
                    ? $"攻击力: {c.Atk}"
                    : $"攻击力: {c.Atk} / 守备力: {c.Def}";

                sb.AppendLine($"    属性种族：{c.AttributeText} | {c.TypeText} | {c.KindText} | {starText} | {statText}");
                sb.AppendLine("    【卡片效果】:");
                if (!string.IsNullOrWhiteSpace(c.Desc))
                {
                    string[] lines = c.Desc.Split('\n');
                    foreach (var l in lines) sb.AppendLine($"        {l}");
                }
                else
                {
                    sb.AppendLine("        (通常怪兽 / 无效果)");
                }
                sb.AppendLine("--------------------------------------------------------------------------------");
                sb.AppendLine();
            }

            // 2. 魔法卡
            sb.AppendLine("################################################################################");
            sb.AppendLine($"【魔法卡一览】(共 {spellCards.Count} 种)");
            sb.AppendLine("################################################################################");
            sb.AppendLine();

            for (int i = 0; i < spellCards.Count; i++)
            {
                var c = spellCards[i];
                sb.AppendLine($"[{i + 1}] {c.Name} (卡片编号: {c.Mrk})");
                sb.AppendLine(onlyOwned ? $"    持有数量：{c.Count} 张" : $"    当前持有：{(c.Count > 0 ? $"{c.Count} 张" : "未持有")}");
                sb.AppendLine($"    卡片种类：{c.KindText}");
                sb.AppendLine("    【卡片效果】:");
                if (!string.IsNullOrWhiteSpace(c.Desc))
                {
                    string[] lines = c.Desc.Split('\n');
                    foreach (var l in lines) sb.AppendLine($"        {l}");
                }
                else
                {
                    sb.AppendLine("        (无特别说明)");
                }
                sb.AppendLine("--------------------------------------------------------------------------------");
                sb.AppendLine();
            }

            // 3. 陷阱卡
            sb.AppendLine("################################################################################");
            sb.AppendLine($"【陷阱卡一览】(共 {trapCards.Count} 种)");
            sb.AppendLine("################################################################################");
            sb.AppendLine();

            for (int i = 0; i < trapCards.Count; i++)
            {
                var c = trapCards[i];
                sb.AppendLine($"[{i + 1}] {c.Name} (卡片编号: {c.Mrk})");
                sb.AppendLine(onlyOwned ? $"    持有数量：{c.Count} 张" : $"    当前持有：{(c.Count > 0 ? $"{c.Count} 张" : "未持有")}");
                sb.AppendLine($"    卡片种类：{c.KindText}");
                sb.AppendLine("    【卡片效果】:");
                if (!string.IsNullOrWhiteSpace(c.Desc))
                {
                    string[] lines = c.Desc.Split('\n');
                    foreach (var l in lines) sb.AppendLine($"        {l}");
                }
                else
                {
                    sb.AppendLine("        (无特别说明)");
                }
                sb.AppendLine("--------------------------------------------------------------------------------");
                sb.AppendLine();
            }

            exportText = sb.ToString();
            return true;
        }

        private static bool TryExtractAllUserDecksMainThread(out string exportText, out int deckCount)
        {
            exportText = "";
            deckCount = 0;

            var content = Content.Instance;
            if (content == null) return false;

            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine("                   游戏王：决斗链接 - 玩家所有已保存卡组一览");
            sb.AppendLine("================================================================================");
            sb.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("================================================================================");
            sb.AppendLine();

            var exportedDeckIds = new HashSet<string>();

            // Strategy 1: Check ClientWork for deck path
            string deckPath = "deck";
            try
            {
                string p = DeckInfo.CLIENTWORK_PATH_DECK;
                if (!string.IsNullOrWhiteSpace(p)) deckPath = p;
            }
            catch { }

            try
            {
                var deckDict = ClientWork.getDictionaryByJsonPath(deckPath, null);
                if (deckDict != null)
                {
                    foreach (var keyObj in deckDict.Keys)
                    {
                        string deckKey = keyObj?.ToString();
                        if (string.IsNullOrWhiteSpace(deckKey) || exportedDeckIds.Contains(deckKey)) continue;

                        DeckInfo info = null;
                        try { info = DeckInfo.GetClientWorkDeck(deckPath, deckKey); } catch { }

                        if (info != null && (info.mainDeck?.Count > 0 || info.extraDeck?.Count > 0))
                        {
                            exportedDeckIds.Add(deckKey);
                            deckCount++;
                            AppendDeckDetails(sb, deckCount, info, content, $"已保存卡组 #{deckKey}");
                        }
                    }
                }
            }
            catch (Exception exCw)
            {
                MelonLogger.Msg($"[CardExporter] User deck ClientWork error: {exCw.Message}");
            }

            // Strategy 2: Check current DeckEdit2ViewController if available
            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                Il2CppYgomSystem.UI.ViewControllerManager contentMgr;
                if (namedMgr != null && namedMgr.TryGetValue("content", out contentMgr) && contentMgr != null)
                {
                    var topVc = contentMgr.GetStackTopViewController();
                    var deckVc = topVc?.TryCast<DeckEdit2ViewController>();
                    if (deckVc != null && deckVc.deckInfo != null)
                    {
                        string currentId = deckVc.deckInfo.DeckId.ToString();
                        if (!exportedDeckIds.Contains(currentId))
                        {
                            exportedDeckIds.Add(currentId);
                            deckCount++;
                            AppendDeckDetails(sb, deckCount, deckVc.deckInfo, content, "当前编辑器出战卡组");
                        }
                    }
                }
            }
            catch { }

            // If still no custom decks found, fallback to reporting available initial decks
            if (deckCount == 0)
            {
                sb.AppendLine("【提示】未在当前缓存中检索到自定义保存卡组，为您列出已解锁角色的初始卡组配方：");
                sb.AppendLine();
                for (int cid = 1; cid <= 120; cid++)
                {
                    try
                    {
                        var initDeck = CharaDeckData.GetInitialCharaDeck(cid);
                        if (initDeck != null && initDeck.Count > 0)
                        {
                            string charaName = LabelExtractor.ResolveCharaName(cid) ?? $"角色 #{cid}";
                            deckCount++;
                            sb.AppendLine($"【卡组 {deckCount}】{charaName} - 角色初始卡组");
                            sb.AppendLine($"主卡组 ({initDeck.Count} 种卡片):");
                            int idx = 1;
                            foreach (var pair in initDeck)
                            {
                                int mrk = 0;
                                int num = 1;
                                if (pair.Key != null) int.TryParse(pair.Key.ToString(), out mrk);
                                if (pair.Value != null) int.TryParse(pair.Value.ToString(), out num);
                                if (mrk <= 0) continue;
                                string cardName = content.GetName(mrk);
                                sb.AppendLine($"  [{idx++}] {cardName} (编号: {mrk}) x{num}");
                            }
                            sb.AppendLine("--------------------------------------------------------------------------------");
                            sb.AppendLine();
                        }
                    }
                    catch { }
                }
            }

            if (deckCount == 0) return false;

            exportText = sb.ToString();
            return true;
        }

        private static bool TryExtractAllGameRecipesMainThread(out string exportText, out int recipeCount)
        {
            exportText = "";
            recipeCount = 0;

            var content = Content.Instance;
            if (content == null) return false;

            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine("                   游戏王：决斗链接 - 官方预组与推荐卡组配方总览");
            sb.AppendLine("================================================================================");
            sb.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("================================================================================");
            sb.AppendLine();

            // 1. Structure Decks (预组商店卡组)
            sb.AppendLine("################################################################################");
            sb.AppendLine("【一、 官方预组卡组 (Structure Decks)】");
            sb.AppendLine("################################################################################");
            sb.AppendLine();

            string structPath = "structure";
            try
            {
                string p = DeckInfo.CLIENTWORK_PATH_STRUCTURE;
                if (!string.IsNullOrWhiteSpace(p)) structPath = p;
            }
            catch { }

            try
            {
                var structDict = ClientWork.getDictionaryByJsonPath(structPath, null);
                if (structDict != null)
                {
                    foreach (var keyObj in structDict.Keys)
                    {
                        string sKey = keyObj?.ToString();
                        DeckInfo info = null;
                        try { info = DeckInfo.GetClientWorkDeck(structPath, sKey); } catch { }

                        if (info != null && (info.mainDeck?.Count > 0 || info.extraDeck?.Count > 0))
                        {
                            recipeCount++;
                            AppendDeckDetails(sb, recipeCount, info, content, $"官方预组 #{sKey}");
                        }
                    }
                }
            }
            catch { }

            // 2. All Characters' Starter Decks (全角色官方初始卡组)
            sb.AppendLine("################################################################################");
            sb.AppendLine("【二、 全角色官方初始配置卡组 (Starter Decks)】");
            sb.AppendLine("################################################################################");
            sb.AppendLine();

            for (int cid = 1; cid <= 150; cid++)
            {
                try
                {
                    var initDeck = CharaDeckData.GetInitialCharaDeck(cid);
                    if (initDeck != null && initDeck.Count > 0)
                    {
                        string charaName = LabelExtractor.ResolveCharaName(cid) ?? $"角色 #{cid}";
                        recipeCount++;
                        sb.AppendLine($"【配方 {recipeCount}】{charaName} - 官方初始卡组 (角色ID: {cid})");
                        sb.AppendLine($"主卡组 ({initDeck.Count} 种卡片):");
                        int idx = 1;
                        foreach (var pair in initDeck)
                        {
                            int mrk = 0;
                            int num = 1;
                            if (pair.Key != null) int.TryParse(pair.Key.ToString(), out mrk);
                            if (pair.Value != null) int.TryParse(pair.Value.ToString(), out num);
                            if (mrk <= 0) continue;
                            string cardName = content.GetName(mrk);
                            sb.AppendLine($"  [{idx++}] {cardName} (编号: {mrk}) x{num}");
                        }
                        sb.AppendLine("--------------------------------------------------------------------------------");
                        sb.AppendLine();
                    }
                }
                catch { }
            }

            // 3. Rental Decks (租借卡组 / 竞技场推荐)
            sb.AppendLine("################################################################################");
            sb.AppendLine("【三、 官方租借与活动推荐卡组 (Rental & Event Decks)】");
            sb.AppendLine("################################################################################");
            sb.AppendLine();

            string rentalPath = "rental";
            try
            {
                string p = DeckInfo.CLIENTWORK_PATH_RENTAL;
                if (!string.IsNullOrWhiteSpace(p)) rentalPath = p;
            }
            catch { }

            try
            {
                var rentalDict = ClientWork.getDictionaryByJsonPath(rentalPath, null);
                if (rentalDict != null)
                {
                    foreach (var keyObj in rentalDict.Keys)
                    {
                        string rKey = keyObj?.ToString();
                        DeckInfo info = null;
                        try { info = DeckInfo.GetRentalDeck(rKey); } catch { }
                        if (info == null)
                        {
                            try { info = DeckInfo.GetClientWorkDeck(rentalPath, rKey); } catch { }
                        }

                        if (info != null && (info.mainDeck?.Count > 0 || info.extraDeck?.Count > 0))
                        {
                            recipeCount++;
                            AppendDeckDetails(sb, recipeCount, info, content, $"租借推荐卡组 #{rKey}");
                        }
                    }
                }
            }
            catch { }

            if (recipeCount == 0) return false;

            exportText = sb.ToString();
            return true;
        }

        private static void AppendDeckDetails(StringBuilder sb, int index, DeckInfo info, Content content, string fallbackTitle)
        {
            string deckName = !string.IsNullOrWhiteSpace(info.Name) ? info.Name : fallbackTitle;
            string charaName = LabelExtractor.ResolveCharaName(info.CharaId) ?? $"角色 #{info.CharaId}";
            string skillText = info.Skill > 0 ? $"技能ID: {info.Skill}" : "未配置技能";

            sb.AppendLine($"【卡组 {index}】{deckName}");
            sb.AppendLine($"所属角色：{charaName} | {skillText} | 主卡组：{info.mainDeck?.Count ?? 0} 张 | 额外：{info.extraDeck?.Count ?? 0} 张");
            sb.AppendLine();

            // Main Deck
            var main = info.mainDeck;
            if (main != null && main.Count > 0)
            {
                sb.AppendLine($"--- 主卡组 ({main.Count} 张) ---");
                // Group duplicates
                var counts = new Dictionary<int, int>();
                var order = new List<int>();
                for (int i = 0; i < main.Count; i++)
                {
                    int mrk = main[i].Mrk;
                    if (!counts.ContainsKey(mrk))
                    {
                        counts[mrk] = 0;
                        order.Add(mrk);
                    }
                    counts[mrk]++;
                }

                int cardIdx = 1;
                foreach (int mrk in order)
                {
                    string cardName = content.GetName(mrk);
                    sb.AppendLine($"  [{cardIdx++}] {cardName} (编号: {mrk}) x{counts[mrk]}");
                }
            }

            // Extra Deck
            var extra = info.extraDeck;
            if (extra != null && extra.Count > 0)
            {
                sb.AppendLine($"--- 额外卡组 ({extra.Count} 张) ---");
                var counts = new Dictionary<int, int>();
                var order = new List<int>();
                for (int i = 0; i < extra.Count; i++)
                {
                    int mrk = extra[i].Mrk;
                    if (!counts.ContainsKey(mrk))
                    {
                        counts[mrk] = 0;
                        order.Add(mrk);
                    }
                    counts[mrk]++;
                }

                int cardIdx = 1;
                foreach (int mrk in order)
                {
                    string cardName = content.GetName(mrk);
                    sb.AppendLine($"  [{cardIdx++}] {cardName} (编号: {mrk}) x{counts[mrk]}");
                }
            }

            sb.AppendLine("--------------------------------------------------------------------------------");
            sb.AppendLine();
        }

        #endregion

        #region Background Save Worker

        private static void StartWorkerThread(ExportContext ctx)
        {
            var th = new Thread(new ParameterizedThreadStart(SaveFileWorkerEntry));
            th.IsBackground = true;
            th.Start(ctx);
        }

        private static void SaveFileWorkerEntry(object state)
        {
            var ctx = state as ExportContext;
            if (ctx == null)
            {
                _isExporting = false;
                return;
            }

            try
            {
                SaveFileWorker(ctx);
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[CardExporter] Worker error: {ex.Message}");
                DebugLogger.Log(LogCategory.Handler, "CardExporter", $"Worker error: {ex}");
                ScreenReader.Say($"导出过程出错：{ex.Message}");
            }
            finally
            {
                _isExporting = false;
            }
        }

        private static void SaveFileWorker(ExportContext ctx)
        {
            string initialDir = GetInitialDirectory();
            string defaultFileName = ctx.DefaultFileName ?? $"游戏王决斗链接_导出_{DateTime.Now:yyyyMMdd_HHmmss}.txt";

            string chosenPath = ShowSubprocessSaveDialog(initialDir, defaultFileName, ctx.DialogTitle);
            if (string.IsNullOrEmpty(chosenPath))
            {
                try
                {
                    string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    string fallbackPath = Path.Combine(desktopDir, defaultFileName);
                    File.WriteAllText(fallbackPath, ctx.Text, Encoding.UTF8);
                    MelonLogger.Msg($"[CardExporter] Dialog closed, fallback saved to: {fallbackPath}");
                    ScreenReader.Say($"选择窗口已关闭。已为您自动保存一份至桌面：{defaultFileName}。");
                    return;
                }
                catch (Exception exFb)
                {
                    MelonLogger.Msg($"[CardExporter] Desktop fallback failed: {exFb.Message}");
                    ScreenReader.Say("已取消导出。");
                    return;
                }
            }

            try
            {
                string dir = Path.GetDirectoryName(chosenPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                    File.WriteAllText(ConfigFilePath, dir, Encoding.UTF8);
                }
            }
            catch { }

            File.WriteAllText(chosenPath, ctx.Text, Encoding.UTF8);

            DebugLogger.Log(LogCategory.Handler, "CardExporter", $"Exported successfully to: {chosenPath}");
            string successMsg = !string.IsNullOrWhiteSpace(ctx.SuccessMessage)
                ? $"{ctx.SuccessMessage} 已保存至 {Path.GetFileName(chosenPath)}。"
                : $"导出完成！已保存至 {Path.GetFileName(chosenPath)}。";

            ScreenReader.Say(successMsg);
        }

        private static string GetInitialDirectory()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string saved = File.ReadAllText(ConfigFilePath, Encoding.UTF8).Trim();
                    if (!string.IsNullOrEmpty(saved) && Directory.Exists(saved))
                        return saved;
                }
            }
            catch { }

            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        private static string ShowSubprocessSaveDialog(string initialDir, string defaultFileName, string dialogTitle = null)
        {
            string title = !string.IsNullOrWhiteSpace(dialogTitle) ? dialogTitle : "请选择导出文件保存位置";
            try
            {
                string helperPath = Path.Combine(ConfigDir, "DuelLinksFilePicker.exe");
                string resultFile = Path.Combine(ConfigDir, "DuelLinksAccess_picked_path.tmp");

                if (File.Exists(helperPath))
                {
                    try { if (File.Exists(resultFile)) File.Delete(resultFile); } catch { }

                    MelonLogger.Msg($"[CardExporter] Launching helper: {helperPath}");
                    var psi = new ProcessStartInfo
                    {
                        FileName = helperPath,
                        Arguments = $"\"{initialDir}\" \"{defaultFileName}\" \"{resultFile}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    };

                    using var proc = Process.Start(psi);
                    if (proc != null)
                    {
                        string output = proc.StandardOutput.ReadToEnd()?.Trim();
                        proc.WaitForExit();

                        if (File.Exists(resultFile))
                        {
                            try
                            {
                                string fileContent = File.ReadAllText(resultFile, Encoding.UTF8).Trim();
                                File.Delete(resultFile);
                                if (!string.IsNullOrWhiteSpace(fileContent)) return fileContent;
                            }
                            catch { }
                        }

                        if (!string.IsNullOrWhiteSpace(output)) return output;
                        return null;
                    }
                }

                // Fallback: PowerShell dialog
                MelonLogger.Msg("[CardExporter] Helper not found, falling back to powershell dialog");
                string safeDir = (initialDir ?? "").Replace("'", "''");
                string safeFile = (defaultFileName ?? "").Replace("'", "''");
                string safeTitle = title.Replace("'", "''");

                string script = $@"
Add-Type -AssemblyName System.Windows.Forms
$dlg = New-Object System.Windows.Forms.SaveFileDialog
$dlg.Title = '{safeTitle}'
$dlg.Filter = '纯文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*'
$dlg.FileName = '{safeFile}'
$dlg.InitialDirectory = '{safeDir}'
$dlg.RestoreDirectory = $true
if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {{
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    Write-Output $dlg.FileName
}}
";
                string base64Script = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

                var psiPs = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-STA -NoProfile -EncodedCommand {base64Script}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using var procPs = Process.Start(psiPs);
                if (procPs == null) return null;

                string psOutput = procPs.StandardOutput.ReadToEnd()?.Trim();
                procPs.WaitForExit();

                if (!string.IsNullOrWhiteSpace(psOutput)) return psOutput;
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[CardExporter] ShowSubprocessSaveDialog error: {ex.Message}");
            }

            return null;
        }

        #endregion

        #region Inner Types

        private class CardRecord
        {
            public int Mrk;
            public string Name;
            public int Count;
            public Content.Kind Kind;
            public string KindText;
            public string AttributeText;
            public string TypeText;
            public int Level;
            public int Rank;
            public int LinkNum;
            public int Atk;
            public int Def;
            public string Desc;
        }

        private class ExportContext
        {
            public string Text;
            public int Distinct;
            public int Copies;
            public string DefaultFileName;
            public string DialogTitle;
            public string SuccessMessage;
        }

        #endregion
    }
}
