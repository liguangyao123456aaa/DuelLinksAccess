using System;
using System.Collections.Generic;
using Il2CppYgomGame.Card;

namespace DuelLinksAccess
{
    /// <summary>
    /// Shared card formatting utilities for accessibility announcements.
    /// Used by DeckEditHandler, ShopHandler, TicketExchangeHandler, and others
    /// that need to read card names and stats via the Content database.
    /// </summary>
    public static class CardFormatter
    {
        /// <summary>
        /// Compact card announcement: name, type info, stats.
        /// Example: "Dark Magician, Dark Spellcaster, Level 7, ATK 2500 DEF 2100"
        /// </summary>
        public static string FormatCompact(int mrk)
        {
            if (mrk <= 0 || mrk >= 100000) return Loc.Get("duel_unknown_card");

            try
            {
                var content = Content.Instance;
                if (content == null) return GetName(mrk);

                string name = content.GetName(mrk);
                if (string.IsNullOrWhiteSpace(name))
                    name = Loc.Get("duel_unknown_card");

                var kind = content.GetKind(mrk);

                if (IsMonsterKind(kind))
                {
                    var attr = content.GetAttr(mrk);
                    var type = content.GetType(mrk);
                    int level = content.GetLevel(mrk);
                    int rank = content.GetRank(mrk);
                    int link = content.GetLinkNum(mrk);
                    int atk = content.GetAtk(mrk);
                    int def = content.GetDef2(mrk);

                    string attrText = content.GetAttributeText(attr)?.Trim();
                    string typeText = content.GetTypeText(type)?.Trim();
                    string kindText = content.GetKindText(kind)?.Trim();
                    if (!string.IsNullOrEmpty(kindText))
                        kindText = kindText.TrimStart('/', ' ');

                    var metaParts = new List<string>();
                    if (!string.IsNullOrEmpty(attrText)) metaParts.Add(attrText);
                    if (!string.IsNullOrEmpty(typeText)) metaParts.Add(typeText);
                    if (!string.IsNullOrEmpty(kindText)) metaParts.Add(kindText);
                    string meta = string.Join(" ", metaParts);

                    string progression = CardProgressionFormatter.Format(
                        level, rank, link);
                    string combatStats = CardProgressionFormatter
                        .FormatCombatStats(atk, def, link);

                    var resParts = new List<string> { name };
                    if (!string.IsNullOrEmpty(meta)) resParts.Add(meta);
                    if (!string.IsNullOrEmpty(progression)) resParts.Add(progression);
                    if (!string.IsNullOrEmpty(combatStats)) resParts.Add(combatStats);

                    return string.Join(", ", resParts);
                }
                else
                {
                    var icon = content.GetIcon(mrk);
                    string kindText = content.GetKindText(kind)?.Trim();
                    if (!string.IsNullOrEmpty(kindText))
                        kindText = kindText.TrimStart('/', ' ');

                    if (icon != Content.Icon.Null)
                    {
                        string iconText = content.GetIconText(icon)?.Trim();
                        return $"{name}, {iconText} {kindText}".TrimEnd(' ', ',');
                    }

                    return $"{name}, {kindText}".TrimEnd(' ', ',');
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardFormatter",
                    $"FormatCompact error for mrk={mrk}: {ex.Message}");
                return GetName(mrk);
            }
        }

        /// <summary>
        /// Verbose card announcement: compact info + description.
        /// </summary>
        public static string FormatVerbose(int mrk)
        {
            if (mrk <= 0 || mrk >= 100000) return Loc.Get("duel_unknown_card");

            string compact = FormatCompact(mrk);

            try
            {
                var content = Content.Instance;
                string desc = content?.GetDesc(mrk);

                var parts = new List<string> { compact };

                if (!string.IsNullOrEmpty(desc))
                    parts.Add(desc);

                return string.Join(". ", parts);
            }
            catch
            {
                return compact;
            }
        }

        /// <summary>
        /// Gets a card's name from the Content database.
        /// </summary>
        public static string GetName(int mrk)
        {
            if (mrk <= 0 || mrk >= 100000) return Loc.Get("duel_unknown_card");

            try
            {
                string name = Content.Instance?.GetName(mrk);
                return string.IsNullOrWhiteSpace(name) ? Loc.Get("duel_unknown_card") : name;
            }
            catch
            {
                return Loc.Get("duel_unknown_card");
            }
        }

        /// <summary>
        /// Checks if a Content.Kind value represents a monster card.
        /// </summary>
        public static bool IsMonsterKind(Content.Kind kind)
        {
            return kind != Content.Kind.Magic
                && kind != Content.Kind.Trap;
        }

    }
}
