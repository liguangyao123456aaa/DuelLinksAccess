using System;
using MelonLoader;

namespace DuelLinksAccess
{
    /// <summary>
    /// Snapshot of one card at a moment in time. Backed by CardRoot for
    /// field cards, HandInfo + Content for hand cards. Returned by
    /// DuelState's per-card accessors. Null Name means the card-db id
    /// couldn't be resolved (e.g., face-down opponent card with mrk=0).
    /// </summary>
    public readonly record struct CardSnapshot(
        int UniqueId,
        int CardDbId,
        string Name,
        int Atk,
        int Def,
        int Level,
        bool IsAttack,
        bool IsFaceUp);

    /// <summary>
    /// Canonical read-only adapter for duel state. Backed by the visual
    /// layer (DuelGameObjectManager.cardRoots, HandCardManager) and
    /// event-tracked counters (phase, turn, LP) — both populated identically
    /// in single-player and PvP/Ranked.
    ///
    /// The motivation: the engine's DLL_Duel* state queries are HOLLOW in
    /// PvP because the server runs the duel logic — DLL_DuelGetCardNum,
    /// DLL_DuelGetCardUniqueID, DLL_DuelComGetCommandMask,
    /// DLL_DuelGetCurrentPhase, DLL_DuelGetMyPlayerNum, etc. all return 0
    /// or stale values. The visual layer (3D world cards) and the
    /// RunEffect event stream ARE the source of truth, and they're equally
    /// populated in single-player. So routing every read through this
    /// adapter gives us one code path that works in both modes.
    ///
    /// Consumers (DuelFieldNavigator, DuelEventAnnouncer, DuelHandler)
    /// query DuelState instead of calling Engine APIs directly. Action
    /// dispatch (TapHandCard, OnDoCardCommand, CardCommand.OnCommand, etc.)
    /// stays where it is — those are commands we issue, not reads.
    ///
    /// Wiring: DuelEventAnnouncer.OnRunEffect calls DuelState.OnRunEffect
    /// from its Harmony postfix entry, so DuelState's tracked counters
    /// always reflect the most recent server-pushed event.
    /// </summary>
    public static class DuelState
    {
        #region Engine locate constants

        private const int LocateHand = 13;
        private const int LocateExtra = 14;
        private const int LocateDeck = 15;
        private const int LocateGrave = 16;

        // Field locates for monster / spell zones (Speed Duel: 3 slots each).
        // The engine fills monsters at 2 → 3 → 1 (middle, right, left) and
        // spells at 9 → 10 → 8. EMZ slots are loc=5 (left) and loc=6 (right);
        // shared between players, populated by Fusion/Synchro/Xyz/Link summons.
        private static readonly int[] MonsterLocates = { 2, 3, 1 };
        private static readonly int[] SpellLocates = { 9, 10, 8 };
        private static readonly int[] EMZLocates = { 5, 6 };

        #endregion

        #region Event-tracked state

        private static int _localSeat = -1;
        private static Il2CppYgomGame.Duel.Engine.Phase _currentPhase
            = Il2CppYgomGame.Duel.Engine.Phase.Null;
        private static readonly DuelTurnTracker _turnTracker = new();
        private static readonly int[] _lp = { -1, -1 };
        private static bool _inDuel;
        private static bool _duelEnded;
        private static bool _isPvPEngineLocked;
        private static readonly System.Collections.Generic.List<int>[] _trackedGraveCardIds = {
            new System.Collections.Generic.List<int>(),
            new System.Collections.Generic.List<int>()
        };

        public static void TrackGraveCard(int player, int cardDbId)
        {
            if (player != 0 && player != 1) return;
            if (cardDbId <= 0 || cardDbId >= 100000) return;
            var list = _trackedGraveCardIds[player];
            // If the card is already at the top (index 0), do not duplicate insert
            if (list.Count > 0 && list[0] == cardDbId) return;

            // Insert newest at beginning so slot 0 is the top of graveyard
            list.Insert(0, cardDbId);
            string name = ResolveCardName(cardDbId) ?? Loc.Get("duel_unknown_card");
            MelonLoader.MelonLogger.Msg($"[DuelState] TrackGraveCard: p={player} cid={cardDbId} name={name} totalTracked={list.Count}");
        }

        public static void UntrackGraveCard(int player, int cardDbId)
        {
            if (player != 0 && player != 1) return;
            if (cardDbId <= 0 || cardDbId >= 100000) return;
            var list = _trackedGraveCardIds[player];
            bool removed = list.Remove(cardDbId);
            if (removed)
            {
                string name = ResolveCardName(cardDbId) ?? Loc.Get("duel_unknown_card");
                MelonLoader.MelonLogger.Msg($"[DuelState] UntrackGraveCard: p={player} cid={cardDbId} name={name} totalTracked={list.Count}");
            }
        }


        #endregion

        #region Public state properties

        public static bool InDuel => _inDuel;
        public static bool DuelEnded => _duelEnded;
        public static Il2CppYgomGame.Duel.Engine.Phase CurrentPhase => _currentPhase;
        public static int CurrentTurnPlayer => _turnTracker.CurrentPlayer;
        public static int TurnNumber => _turnTracker.TurnNumber;

        /// <summary>Engine's current input-wait mode (BattlePhase, CheckChain, LockOn, etc.).</summary>
        public static Il2CppYgomGame.Duel.Engine.MenuActType InputType
        {
            get
            {
                try
                {
                    return Il2CppYgomGame.Duel.DuelClient.instance?.worker2d?.curInputType
                        ?? Il2CppYgomGame.Duel.Engine.MenuActType.Null;
                }
                catch { return Il2CppYgomGame.Duel.Engine.MenuActType.Null; }
            }
        }

        #endregion

        #region Seat resolution

        /// <summary>
        /// Server seat assigned to the local player (0 or 1). Detected from
        /// the visual layer: nearHandCard is always the local viewer's hand,
        /// so the CardPlace.team of its first card IS our server seat. This
        /// works at duel start once hands are dealt — well before any user
        /// action — and works identically in single-player and PvP.
        ///
        /// Engine APIs (DLL_DuelGetMyPlayerNum, DLL_DuelMyself) are kept as
        /// a last-resort fallback; they're correct in single-player and
        /// lie in PvP, so we only consult them when the visual layer hasn't
        /// initialized yet (very brief window at duel start).
        /// </summary>
        public static int MyPlayerNum()
        {
            if (_localSeat == 0 || _localSeat == 1) return _localSeat;

            // Primary: nearHandCard's first card → its CardPlace.team
            try
            {
                var client = Il2CppYgomGame.Duel.DuelClient.instance;
                var hcm = client?.worker3d?.handCardManager;
                var nearHand = hcm?.nearHandCard;
                var list = nearHand?.m_InfoList;
                if (list != null && list.Count > 0)
                {
                    int uid = list[0].m_UniqueId;
                    var root = client.worker3d.goManager?.FindCardInstance(uid);
                    var place = root?.toLocator?.cardPlace;
                    if (place != null)
                    {
                        int seat = place.team;
                        if (seat == 0 || seat == 1)
                        {
                            _localSeat = seat;
                            DebugLogger.Log(LogCategory.Game, "DuelState",
                                $"Local seat detected: {seat} (via nearHandCard)");
                            return seat;
                        }
                    }
                }
            }
            catch { }

            // Secondary: any CardRoot whose CardStatus.nearSide is true
            try
            {
                var roots = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager?.cardRoots;
                if (roots != null)
                {
                    for (int i = 0; i < roots.Count; i++)
                    {
                        var root = roots[i];
                        var status = root?.cardStatus;
                        if (status != null && status.nearSide)
                        {
                            var place = root.toLocator?.cardPlace;
                            if (place != null && (place.team == 0 || place.team == 1))
                            {
                                _localSeat = place.team;
                                DebugLogger.Log(LogCategory.Game, "DuelState",
                                    $"Local seat detected: {_localSeat} (via nearSide CardRoot)");
                                return _localSeat;
                            }
                        }
                    }
                }
            }
            catch { }

            // Last resort: engine API (correct in single-player, wrong in PvP)
            try
            {
                int p = Il2CppYgomGame.Duel.Engine.DLL_DuelGetMyPlayerNum();
                if (p == 0 || p == 1) return p;
            }
            catch { }

            return 0;
        }

        public static int OppPlayerNum() => 1 - MyPlayerNum();

        /// <summary>True iff the given engine/event player number is the local player.</summary>
        public static bool IsMine(int eventPlayer) => eventPlayer == MyPlayerNum();

        #endregion

        #region Vitals

        /// <summary>
        /// Returns LP for the given player from event-tracked state.
        /// Falls back to DLL_DuelGetLP if events haven't initialized.
        /// </summary>
        public static int GetLP(int player)
        {
            if (player < 0 || player > 1) return -1;
            if (_lp[player] >= 0) return _lp[player];
            try { return Il2CppYgomGame.Duel.Engine.DLL_DuelGetLP(player); }
            catch { return -1; }
        }

        #endregion

        #region Card lookups

        /// <summary>
        /// Returns the card at the given field position, or null if empty.
        /// "player" is the engine/event server seat. "locate" is the
        /// engine locate value (1/2/3 monsters, 8/9/10 spells, 6 EMZ, etc.).
        /// "slot" is the within-zone index, always 0 for one-slot field zones.
        /// </summary>
        public static CardSnapshot? GetFieldCard(int player, int locate, int slot)
        {
            // Stack zones (Extra Deck, Graveyard, Deck) are multi-card zones.
            // Never let them be short-circuited or contaminated by arbitrary/dummy CardRoots.
            if (IsStackZone(locate))
            {
                return GetStackCard(player, locate, slot);
            }

            var root = FindFieldCardRoot(player, locate, slot);
            if (root == null)
            {

                // Non-stack field zone (monsters 1-3, EMZ 5-6, spells 8-12).
                // Each locate is single-occupancy, so slot is always 0 and
                // ANY CardRoot whose (team, position) matches is the right
                // card regardless of its locator.index. Fusion summons and
                // other special summons can produce a CardRoot whose
                // locator.index != 0 (overlay-material bookkeeping etc.),
                // causing the strict FindFieldCardRoot lookup to miss the
                // monster that's actually there. Fall through to a
                // position-only walk to recover that case.
                if (slot == 0)
                {
                    try
                    {
                        var roots = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager?.cardRoots;
                        if (roots != null)
                        {
                            for (int i = 0; i < roots.Count; i++)
                            {
                                var r = roots[i];
                                var place = r?.toLocator?.cardPlace;
                                if (place != null
                                    && place.team == player
                                    && place.position == locate)
                                {
                                    root = r;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                    if (root == null) return null;
                }
                else
                {
                    return null;
                }
            }

            // Face-up determination: engine's DLL_DuelGetCardFace is authoritative
            // in single-player (1=face-up, 0=face-down). In PvP it returns 0
            // everywhere, so we fall back to the visual layer when the engine
            // appears hollow.
            bool isFaceUp = false;
            int engineFace = -1;
            try
            {
                engineFace = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardFace(player, locate, slot);
            }
            catch { }

            if (engineFace == 1)
            {
                isFaceUp = true;
            }
            else if (engineFace == 0 && !IsEngineHollow)
            {
                // Single-player: trust the engine's face=0 answer.
                isFaceUp = false;
            }
            else
            {
                // PvP fallback: visual visibility, picked by zone. For
                // monster zones the source of truth is isMonsterVisible —
                // a SET monster has cardId>0 (we may know our own) but its
                // monster art is hidden, so isMonsterVisible is false. The
                // isStatusVisible flag stays true for set monsters (status
                // indicator on the card back), which is why combining the
                // two flagged set Saggi as "face up". For spell/trap zones
                // we have no equally clean flag; isStatusVisible is the
                // best available proxy and is correct often enough.
                try
                {
                    if (locate >= 1 && locate <= 8)
                        isFaceUp = root.isMonsterVisible;
                    else if (locate >= 9 && locate <= 12)
                        isFaceUp = root.isStatusVisible;
                    else
                        isFaceUp = root.isMonsterVisible || root.isStatusVisible;

                    // Known misread: a face-down SET spell can carry
                    // isStatusVisible=true (2026-07-16 Wave Scramble log —
                    // own set Polymerization announced "Face up"). Log the
                    // candidate flags so the next debug log can verify
                    // whether CardRoot's FlipTurn bool is the real face flag
                    // before we switch to it.
                    DebugLogger.Log(LogCategory.Game, "DuelState",
                        $"PvP face fallback: locate={locate} slot={slot} " +
                        $"statusVis={root.isStatusVisible} monsterVis={root.isMonsterVisible} " +
                        $"flipFlag={root.getParamTypeOpponentIdxAct} " +
                        $"finishFlipTurn={root.finishFlipTurn} -> isFaceUp={isFaceUp}");
                }
                catch { isFaceUp = true; }
            }

            return MakeSnapshot(root, isFaceUp);
        }

        /// <summary>
        /// Dedicated resolver for multi-card stack zones (Extra Deck, Graveyard, Deck).
        /// Completely decoupled from single-occupancy field CardRoot scanning.
        /// </summary>
        private static CardSnapshot? GetStackCard(int player, int locate, int slot)
        {
            if (slot < 0) return null;

            if (locate == LocateExtra)
            {
                // 1. Authoritative Engine Query (Single-player / live engine)
                if (!IsEngineHollow)
                {
                    int uid = 0;
                    try { uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(player, locate, slot); }
                    catch { }
                    if (uid > 0)
                    {
                        var byUid = FindCardRootByUid(uid);
                        if (byUid != null && byUid.cardId > 0 && byUid.cardId < 100000)
                            return MakeSnapshot(byUid, isFaceUp: true);
                        var snap = MakeContentSnapshot(uid);
                        if (snap.HasValue && snap.Value.CardDbId > 0 && snap.Value.CardDbId < 100000)
                            return snap;
                    }
                }

                // 2. Registered Deck Array with dynamic diff (Primary for PvP: excludes summoned extra monsters)
                var fromDeck = GetFilteredRegisteredExtraDeckCard(player, slot);
                if (fromDeck.HasValue && fromDeck.Value.CardDbId > 0 && fromDeck.Value.CardDbId < 100000)
                    return fromDeck;

                // 3. Live 3D stack fallback
                if (TryGetLiveExtraDeckCard(player, slot, out CardSnapshot? liveExtra)
                    && liveExtra.HasValue && liveExtra.Value.CardDbId > 0 && liveExtra.Value.CardDbId < 100000)
                {
                    return liveExtra;
                }

                // 4. General engine fallback
                int fallbackUid = 0;
                try { fallbackUid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(player, locate, slot); }
                catch { }
                if (fallbackUid > 0)
                {
                    var byUid = FindCardRootByUid(fallbackUid);
                    if (byUid != null && byUid.cardId > 0 && byUid.cardId < 100000)
                        return MakeSnapshot(byUid, isFaceUp: true);
                    var snap = MakeContentSnapshot(fallbackUid);
                    if (snap.HasValue && snap.Value.CardDbId > 0 && snap.Value.CardDbId < 100000)
                        return snap;
                }

                return null;
            }

            if (locate == LocateGrave)
            {
                // 1. Authoritative Engine Query (Single-player)
                if (!IsEngineHollow)
                {
                    int uid = 0;
                    try { uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(player, locate, slot); }
                    catch { }
                    if (uid > 0)
                    {
                        var byUid = FindCardRootByUid(uid);
                        if (byUid != null && byUid.cardId > 0 && byUid.cardId < 100000)
                            return MakeSnapshot(byUid, isFaceUp: true);
                        var snap = MakeContentSnapshot(uid);
                        if (snap.HasValue && snap.Value.CardDbId > 0 && snap.Value.CardDbId < 100000)
                            return snap;
                    }
                }

                // 2. Slot 0 is the top card of graveyard. Check 3D CardRoot first.
                if (slot == 0)
                {
                    var topRoot = FindNthStackCardRoot(player, LocateGrave, 0)
                               ?? FindFieldCardRoot(player, LocateGrave, 0)
                               ?? GetStackPlaceInnerCard(player, LocateGrave, 0);
                    if (topRoot != null)
                    {
                        var snap = MakeSnapshot(topRoot, isFaceUp: true);
                        if (snap.CardDbId > 0 && snap.CardDbId < 100000)
                        {
                            // Sync tracked grave in case visual top was generated before event hook
                            if (player == 0 || player == 1)
                            {
                                var list = _trackedGraveCardIds[player];
                                if (list.Count == 0 || list[0] != snap.CardDbId)
                                {
                                    list.Insert(0, snap.CardDbId);
                                    MelonLoader.MelonLogger.Msg($"[DuelState] Sync top 3D card to grave: p={player} cid={snap.CardDbId} name={snap.Name}");
                                }
                            }
                            return snap;
                        }
                    }
                }

                // 3. For all slots (especially slot >= 1 in PvP), resolve from tracked graveyard cards
                if (player == 0 || player == 1)
                {
                    var graveList = _trackedGraveCardIds[player];
                    if (graveList != null && slot < graveList.Count)
                    {
                        int cid = graveList[slot];
                        if (cid > 0 && cid < 100000)
                        {
                            var snap = BuildContentSnapshot(cid);
                            if (snap.HasValue && snap.Value.CardDbId > 0)
                                return snap;
                        }
                    }
                }

                // 4. Try inner cards or Nth root fallback
                var inner = GetStackPlaceInnerCard(player, locate, slot);
                if (inner != null)
                {
                    var snap = MakeSnapshot(inner, isFaceUp: true);
                    if (snap.CardDbId > 0 && snap.CardDbId < 100000) return snap;
                }

                var nth = FindNthStackCardRoot(player, locate, slot);
                if (nth != null)
                {
                    var snap = MakeSnapshot(nth, isFaceUp: true);
                    if (snap.CardDbId > 0 && snap.CardDbId < 100000) return snap;
                }

                return null;
            }

            // Main Deck or other stack zones
            var fromDeckNonExtra = GetRegisteredDeckCard(player, locate, slot);
            if (fromDeckNonExtra.HasValue && fromDeckNonExtra.Value.CardDbId > 0 && fromDeckNonExtra.Value.CardDbId < 100000)
                return fromDeckNonExtra;

            int uidNonExtra = 0;
            try { uidNonExtra = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(player, locate, slot); }
            catch { }
            if (uidNonExtra > 0)
            {
                var byUid = FindCardRootByUid(uidNonExtra);
                if (byUid != null && byUid.cardId > 0 && byUid.cardId < 100000)
                    return MakeSnapshot(byUid, isFaceUp: true);
                var snap = MakeContentSnapshot(uidNonExtra);
                if (snap.HasValue && snap.Value.CardDbId > 0 && snap.Value.CardDbId < 100000)
                    return snap;
            }

            var fallbackRoot = GetStackPlaceInnerCard(player, locate, slot)
                            ?? FindNthStackCardRoot(player, locate, slot);
            if (fallbackRoot != null)
            {
                var snap = MakeSnapshot(fallbackRoot, isFaceUp: true);
                if (snap.CardDbId > 0 && snap.CardDbId < 100000) return snap;
            }

            return null;
        }

        /// <summary>
        /// Returns the hand card at the given slot for the given player, or
        /// null if the slot is empty. "player" is engine/event server seat;
        /// the local player's hand reads through nearHandCard, opponent's
        /// through farHandCard.
        /// </summary>
        public static CardSnapshot? GetHandCard(int player, int slot)
        {
            try
            {
                var hand = GetHandCardsFor(player);
                var list = hand?.m_InfoList;
                if (list == null || slot < 0 || slot >= list.Count) return null;

                var info = list[slot];
                if (info == null) return null;

                int uid = info.m_UniqueId;
                int mrk = info.m_Mrk;
                if (uid <= 0 && mrk <= 0) return null;

                // HandInfo doesn't carry level/atk/def — pull from Content
                // database using the mrk. Spells/traps return 0s here, which
                // is fine; consumers should check the card kind before
                // announcing stats.
                int level = 0, atk = 0, def = 0;
                if (mrk > 0)
                {
                    try
                    {
                        var content = Il2CppYgomGame.Card.Content.Instance;
                        if (content != null)
                        {
                            level = content.GetLevel(mrk);
                            atk = content.GetAtk(mrk);
                            def = content.GetDef2(mrk);
                        }
                    }
                    catch { }
                }

                return new CardSnapshot(uid, mrk, ResolveCardName(mrk),
                    atk, def, level, IsAttack: false, IsFaceUp: false);
            }
            catch { return null; }
        }

        /// <summary>Number of cards in the given player's hand.</summary>
        public static int GetHandSize(int player)
        {
            try
            {
                var hand = GetHandCardsFor(player);
                return hand?.m_InfoList?.Count ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Generic card count for a (player, locate) tuple. Hand routes
        /// through the visual layer; other locates try the engine first,
        /// then check the per-half-field DeckCardPlace for stack zones
        /// (extra deck, deck, grave, banished), and finally fall back to
        /// counting matching CardRoots if everything above is hollow.
        /// </summary>
        public static int GetCardCount(int player, int locate)
        {
            if (locate == LocateHand) return GetHandSize(player);

            // In live engine / single-player, DLL_DuelGetCardNum is authoritative
            if (!IsEngineHollow)
            {
                try
                {
                    int engineCount = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardNum(player, locate);
                    // Critical fix: In PvP before hollow lock settles, engine returns 0 for every locate.
                    // Only return positive count; do not short-circuit a populated stack to 0!
                    if (engineCount > 0) return engineCount;
                }
                catch { }
            }

            if (locate == LocateExtra)
            {
                // Dynamic adaptive count: subtracts summoned/consumed monsters & ignores -1 placeholders
                int filteredCount = GetFilteredRegisteredExtraDeckCount(player);
                if (filteredCount >= 0) return filteredCount;

                if (TryGetLiveExtraDeckCount(player, out int liveExtraCount) && liveExtraCount > 0)
                    return liveExtraCount;
            }

            if (locate == LocateDeck)
            {
                int registered = GetRegisteredDeckCount(player, locate);
                if (registered > 0) return registered;
            }

            if (locate == LocateGrave)
            {
                var place = GetStackPlace(player, LocateGrave);
                int localNum = place?.localCardNum ?? 0;
                int trackedNum = _trackedGraveCardIds[player]?.Count ?? 0;
                int bestGraveCount = Math.Max(localNum, trackedNum);
                if (bestGraveCount > 0) return bestGraveCount;
            }

            int fallbackEngine = 0;
            try { fallbackEngine = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardNum(player, locate); }
            catch { }
            if (fallbackEngine > 0) return fallbackEngine;

            // Per-half-field DeckCardPlace fallback. In PvP the engine hollows
            // out DLL_DuelGetCardNum for every locate including extra deck,
            // and only the *top* card gets a CardRoot in goManager.cardRoots
            // (so visual-walk undercounts a 6-card extra deck as 1). The half-
            // field's DeckCardPlace.innerCards is the full list and is set
            // when the deck is registered, so it works for the local player
            // in PvP. Restricted to stack zones to avoid altering working
            // single-occupancy paths.
            if (IsStackZone(locate))
            {
                var place = GetStackPlace(player, locate);
                if (place != null)
                {
                    try
                    {
                        int n = place.innerCards?.Count ?? 0;
                        if (n > 0) return n;
                        // Some places track count separately from innerCards
                        // populating the list (e.g. while a draw animation is
                        // pending). Fall back to localCardNum.
                        int alt = place.localCardNum;
                        if (alt > 0) return alt;
                    }
                    catch { }
                }
            }

            // Visual fallback — last resort. For non-stack zones this is the
            // primary PvP path. For stack zones it'll typically undercount in
            // PvP (only top-of-stack materialized) but we keep it as a final
            // safety net.
            int count = 0;
            try
            {
                var roots = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager?.cardRoots;
                if (roots != null)
                {
                    for (int i = 0; i < roots.Count; i++)
                    {
                        var root = roots[i];
                        var place = root?.toLocator?.cardPlace;
                        if (place != null && place.team == player && place.position == locate)
                            count++;
                    }
                }
            }
            catch { }
            return count;
        }

        /// <summary>
        /// Returns the per-team DeckCardPlace for the given stack-zone locate
        /// (extra deck, deck, grave, banished). DuelFieldBase exposes a 2-
        /// element halfFields array — one HalfField per player — and each
        /// HalfField holds its side's extraPlace/deckPlace/gravePlace/
        /// excludePlace. Index 0 corresponds to server seat 0 and index 1 to
        /// server seat 1, matching CardPlace.team.
        /// </summary>
        private static Il2CppYgomGame.Duel.DeckCardPlace GetStackPlace(int player, int locate)
        {
            if (player != 0 && player != 1) return null;
            try
            {
                var field = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager?.duelField;
                var halves = field?.halfFields;
                if (halves == null || player >= halves.Count) return null;
                var half = halves[player];
                if (half == null) return null;
                switch (locate)
                {
                    case LocateExtra: return half.extraPlace;
                    case LocateDeck:  return half.deckPlace;
                    case LocateGrave: return half.gravePlace;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Returns the slot-th CardRoot stacked inside a half-field pile
        /// (grave/deck/extra) via DeckCardPlace.innerCards. The pile keeps
        /// its stacked roots here after they leave goManager.cardRoots, so
        /// this is the hollow-PvP content source for the graveyard. Logs the
        /// pile shape (innerCards vs localCardNum) on slot-0 reads so debug
        /// logs prove which source carries grave contents.
        /// </summary>
        private static Il2CppYgomGame.Duel.CardRoot GetStackPlaceInnerCard(
            int player, int locate, int slot)
        {
            try
            {
                var place = GetStackPlace(player, locate);
                if (place == null) return null;
                var inner = place.innerCards;
                int innerCount = inner?.Count ?? 0;

                if (slot == 0)
                {
                    int localNum = -1;
                    try { localNum = place.localCardNum; } catch { }
                    DebugLogger.Log(LogCategory.Game, "DuelState",
                        $"Pile shape p={player} l={locate}: " +
                        $"innerCards={innerCount} localCardNum={localNum}");
                }

                if (inner == null || slot < 0 || slot >= innerCount) return null;
                return inner[slot];
            }
            catch { return null; }
        }

        private static bool TryGetLiveExtraDeckCount(
            int player, out int count)
        {
            count = 0;
            try
            {
                var place = GetStackPlace(player, LocateExtra);
                var cards = place?.innerCards;
                if (!ExtraDeckSourcePolicy.UseLiveStack(
                    place != null,
                    place?.isLoaded == true,
                    cards?.Count ?? 0)) return false;

                count = cards.Count;
                return true;
            }
            catch { return false; }
        }

        private static bool TryGetLiveExtraDeckCard(
            int player, int slot, out CardSnapshot? card)
        {
            card = null;
            if (slot < 0) return false;
            try
            {
                var place = GetStackPlace(player, LocateExtra);
                var cards = place?.innerCards;
                if (!ExtraDeckSourcePolicy.UseLiveStack(
                    place != null,
                    place?.isLoaded == true,
                    cards?.Count ?? 0)) return false;

                // A populated stack is authoritative for slot existence, but
                // the opponent's Extra Deck identities remain hidden.
                if (!ExtraDeckSourcePolicy.CanRevealIdentity(
                    player, MyPlayerNum())) return true;

                if (slot < cards.Count && cards[slot] != null)
                {
                    var snap = MakeSnapshot(cards[slot], isFaceUp: true);
                    if (snap.CardDbId > 0)
                    {
                        card = snap;
                        return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Counts monsters / spells on the given player's field. Folds BOTH
        /// Extra Monster Zone slots (loc=5 left, loc=6 right) into the monster
        /// count so Synchro/Xyz/Link/Fusion summons appear in the F-key summary.
        /// </summary>
        public static void CountFieldCards(int player, out int monsters, out int spells)
        {
            monsters = 0;
            spells = 0;
            foreach (int loc in MonsterLocates)
                if (GetFieldCard(player, loc, 0) != null) monsters++;
            foreach (int emzLoc in EMZLocates)
                if (GetFieldCard(player, emzLoc, 0) != null) monsters++;
            foreach (int loc in SpellLocates)
                if (GetFieldCard(player, loc, 0) != null) spells++;
        }

        #endregion

        #region Commands (engine direct — these still work in PvP)

        public static uint GetCommandMask(int player, int locate, int slot)
        {
            try { return Il2CppYgomGame.Duel.Engine.DLL_DuelComGetCommandMask(player, locate, slot); }
            catch { return 0; }
        }

        public static int GetAttackTargetMask(int player, int locate)
        {
            try { return Il2CppYgomGame.Duel.Engine.DLL_DuelGetAttackTargetMask(player, locate); }
            catch { return 0; }
        }

        #endregion

        #region Event hook (called from DuelEventAnnouncer.OnRunEffect)

        /// <summary>
        /// Updates DuelState's tracked counters from the RunEffect event
        /// stream. Called at the top of DuelEventAnnouncer.OnRunEffect so
        /// every consumer that reads DuelState during the same event cycle
        /// sees the fresh values.
        /// </summary>
        public static void OnRunEffect(int id, int p1, int p2, int p3)
        {
            var viewType = (Il2CppYgomGame.Duel.Engine.ViewType)id;
            switch (viewType)
            {
                case Il2CppYgomGame.Duel.Engine.ViewType.DuelStart:
                    _inDuel = true;
                    _duelEnded = false;
                    _localSeat = -1; // matchmaking may have assigned a new seat
                    _isPvPEngineLocked = false;
                    _currentPhase = Il2CppYgomGame.Duel.Engine.Phase.Null;
                    _turnTracker.Reset();
                    _trackedGraveCardIds[0].Clear();
                    _trackedGraveCardIds[1].Clear();
                    // Don't reset _lp — LifeSet fires BEFORE DuelStart with
                    // the starting values, and we want to keep them.
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.DuelEnd:
                    _duelEnded = true;
                    _inDuel = false;
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.PhaseChange:
                    _currentPhase = (Il2CppYgomGame.Duel.Engine.Phase)p2;
                    _turnTracker.ObservePhase(p1);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.TurnChange:
                    _turnTracker.ObserveTurnChange(p1);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.LifeSet:
                    if (p1 == 0 || p1 == 1) _lp[p1] = p2;
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.LifeDamage:
                    // LifeDamage fires twice per damage event (p3 bit 0 set
                    // on first fire only). p2 is the delta (negative for
                    // damage, positive for recovery).
                    if ((p3 & 1) == 1 && (p1 == 0 || p1 == 1) && _lp[p1] >= 0)
                    {
                        _lp[p1] = Math.Max(0, _lp[p1] + p2);
                    }
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.WaitInput:
                    // Resumed duels never fire DuelStart, so set InDuel here too
                    if (!_inDuel) _inDuel = true;
                    break;
            }
        }

        /// <summary>Reset to a clean pre-duel state.</summary>
        public static void Reset()
        {
            _inDuel = false;
            _duelEnded = false;
            _localSeat = -1;
            _isPvPEngineLocked = false;
            _currentPhase = Il2CppYgomGame.Duel.Engine.Phase.Null;
            _turnTracker.Reset();
            _lp[0] = -1;
            _lp[1] = -1;
            _trackedGraveCardIds[0].Clear();
            _trackedGraveCardIds[1].Clear();
        }

        #endregion

        #region Private helpers

        private static Il2CppYgomGame.Duel.HandCards GetHandCardsFor(int player)
        {
            try
            {
                var hcm = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.handCardManager;
                if (hcm == null) return null;
                // "near" is always the local viewer's hand, "far" is opponent's.
                return player == MyPlayerNum() ? hcm.nearHandCard : hcm.farHandCard;
            }
            catch { return null; }
        }

        private static Il2CppYgomGame.Duel.CardRoot FindFieldCardRoot(
            int player, int locate, int slot)
        {
            try
            {
                var roots = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager?.cardRoots;
                if (roots == null) return null;

                for (int i = 0; i < roots.Count; i++)
                {
                    var root = roots[i];
                    if (root == null) continue;
                    var locator = root.toLocator;
                    var place = locator?.cardPlace;
                    if (place == null) continue;

                    if (place.team == player
                        && place.position == locate
                        && locator.index == slot)
                    {
                        return root;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// True for multi-card zones (extra deck, grave, deck) where the
        /// visual layer's locator.index assignment may not match our
        /// 0-based slot enumeration — engine API is the authority.
        /// </summary>
        private static bool IsStackZone(int locate)
        {
            return locate == LocateExtra
                || locate == LocateGrave
                || locate == LocateDeck;
        }

        /// <summary>
        /// Finds the CardRoot whose uniqueId matches, regardless of its
        /// locator.index or position. Used for stack zones where engine
        /// gives us a uid but FindFieldCardRoot's index match misses.
        /// </summary>
        private static Il2CppYgomGame.Duel.CardRoot FindCardRootByUid(int uid)
        {
            if (uid <= 0) return null;
            try
            {
                var gom = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager;
                return gom?.FindCardInstance(uid);
            }
            catch { return null; }
        }

        /// <summary>
        /// PvP-safe stack-zone slot lookup. Walks cardRoots, collects every
        /// entry whose (team, position) matches, orders by locator.index for
        /// a stable mapping, returns the slot-th match. Used as the fallback
        /// when DLL_DuelGetCardUniqueID is hollow (PvP). Without this, Extra
        /// Deck cards whose locator.index is non-zero (notably when XYZ
        /// monsters are present) read as Empty even though the count is
        /// non-zero.
        ///
        /// Diagnostic: logs the per-call match count and locator.index list
        /// so a debug-mode log session can prove (or disprove) whether the
        /// CardRoots actually exist in PvP. If matchCount=0 the visual layer
        /// doesn't store extra-deck cards in cardRoots at all, and we need a
        /// different strategy (probably reading from a per-player extra-deck
        /// manager). If matchCount > 0 and the user still hears "Empty"
        /// then something downstream of MakeSnapshot is rejecting the
        /// result.
        /// </summary>
        private static Il2CppYgomGame.Duel.CardRoot FindNthStackCardRoot(
            int player, int locate, int slot)
        {
            if (slot < 0) return null;
            try
            {
                var roots = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager?.cardRoots;
                if (roots == null)
                {
                    DebugLogger.Log(LogCategory.Game, "DuelState",
                        $"FindNthStackCardRoot(p={player}, l={locate}, slot={slot}): cardRoots is null");
                    return null;
                }

                // Collect matching locator.index values for diagnostic logging.
                // Also remember the first matching CardRoot's cardId AND
                // cardPlane.cardModel.cardId — the latter is the privacy-
                // gated fallback used by MakeSnapshot; if BOTH are 0 then the
                // visual layer simply doesn't carry the cardId at this point
                // and we need a different data source.
                var indices = new System.Collections.Generic.List<int>();
                int firstCardId = -1, firstModelId = -1, firstUid = -1;
                for (int i = 0; i < roots.Count; i++)
                {
                    var root = roots[i];
                    var locator = root?.toLocator;
                    var place = locator?.cardPlace;
                    if (place != null && place.team == player && place.position == locate)
                    {
                        indices.Add(locator.index);
                        if (firstCardId < 0)
                        {
                            try { firstCardId = root.cardId; } catch { }
                            try { firstUid = root.uniqueId; } catch { }
                            try
                            {
                                var model = root.cardPlane?.cardModel;
                                firstModelId = model?.cardId ?? -1;
                            }
                            catch { }
                        }
                    }
                }

                DebugLogger.Log(LogCategory.Game, "DuelState",
                    $"FindNthStackCardRoot(p={player}, l={locate}, slot={slot}): " +
                    $"matches={indices.Count} indices=[{string.Join(",", indices)}] " +
                    $"firstCardId={firstCardId} firstModelId={firstModelId} firstUid={firstUid}");

                if (slot >= indices.Count) return null;

                // Robust Nth match: collect all unique CardRoots for this (player, locate)
                // In Unity 3D field, stacked cards in graveyard/banished often share locator.index == 0.
                // Strict index-greater-than check fails on slot >= 1! Here we gather distinct roots.
                var matchingList = new System.Collections.Generic.List<Il2CppYgomGame.Duel.CardRoot>();
                for (int i = 0; i < roots.Count; i++)
                {
                    var root = roots[i];
                    var locator = root?.toLocator;
                    var place = locator?.cardPlace;
                    if (place != null && place.team == player && place.position == locate)
                    {
                        if (!matchingList.Contains(root))
                            matchingList.Add(root);
                    }
                }

                if (slot >= 0 && slot < matchingList.Count)
                    return matchingList[slot];

                return null;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelState",
                    $"FindNthStackCardRoot threw: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Builds a CardSnapshot from the Content database for a uniqueId
        /// when no CardRoot exists in the visual layer (extra deck cards
        /// the engine knows about but hasn't materialized as 3D objects).
        /// Returns null if the cardId can't be resolved.
        /// </summary>
        private static CardSnapshot? MakeContentSnapshot(int uid)
        {
            int cardId = 0;
            try
            {
                uint resolved = Il2CppYgomGame.Duel.Engine
                    .DLL_DuelGetCardIDByUniqueID2(uid);
                if (resolved > 0 && resolved < 100000) cardId = (int)resolved;
            }
            catch { }
            return BuildContentSnapshot(cardId, uid);
        }

        /// <summary>
        /// Builds a CardSnapshot for a card identified only by its Content
        /// database id (cardDbId / mrk). Used when reading from the
        /// EngineInitializer's registered deck arrays, where each entry IS
        /// the cardDbId directly — no uniqueId, no CardRoot, no engine
        /// resolver needed. Returns null if cardId is out of range.
        /// </summary>
        private static CardSnapshot? BuildContentSnapshot(int cardId, int uid = 0)
        {
            if (cardId <= 0 || cardId >= 100000) return null;

            int atk = 0, def = 0, level = 0;
            try
            {
                var content = Il2CppYgomGame.Card.Content.Instance;
                if (content != null)
                {
                    atk = content.GetAtk(cardId);
                    def = content.GetDef2(cardId);
                    level = content.GetLevel(cardId);
                }
            }
            catch { }

            return new CardSnapshot(uid, cardId, ResolveCardName(cardId),
                atk, def, level, IsAttack: false, IsFaceUp: true);
        }

        /// <summary>
        /// Returns the inner Il2CppStructArray for the given player's
        /// (main / extra / side) deck from the EngineInitializer's cached
        /// registered-deck data. Outer index convention: 0=main, 1=extra,
        /// 2=side. Returns null when the engineInitializer isn't available,
        /// when the player slot is out of range (we only handle 0/1),
        /// when the deckType is out of range, or when the requested player
        /// isn't the local seat (opponent's deck is server-withheld and the
        /// array is empty client-side anyway — explicit gate keeps us safe
        /// if that ever changes).
        /// </summary>
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>
            GetRegisteredDeckArray(int player, int deckType)
        {
            if (player != 0 && player != 1) return null;
            if (deckType < 0 || deckType > 2) return null;
            try
            {
                var init = Il2CppYgomGame.Duel.DuelClient.GetEngineInitializer();
                if (init == null) return null;

                int localSeat = MyPlayerNum();
                // Privacy gate: allow reading local player's deck even if init.myPlayerNum lags or mismatches
                if (player != localSeat && player != init.myPlayerNum) return null;

                var deck = player switch
                {
                    0 => init.deck0,
                    1 => init.deck1,
                    _ => null
                };
                if (deck != null && deck.Length > deckType && deck[deckType] != null && deck[deckType].Length > 0)
                {
                    return deck[deckType];
                }

                // Fallback: in some PvP rooms, client mounts local deck on deck0 regardless of seat
                var altDeck = player == 0 ? init.deck1 : init.deck0;
                if (altDeck != null && altDeck.Length > deckType && altDeck[deckType] != null && altDeck[deckType].Length > 0)
                {
                    return altDeck[deckType];
                }

                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Snapshot for a card identified only by its position in the
        /// registered-deck array. Locate maps to deck type: Extra (14) →
        /// outer[1], Deck (15) → outer[0]. Used for stack zones in PvP
        /// where the visual layer doesn't materialize all cards and the
        /// engine's DLL_Duel queries are hollow.
        /// </summary>


        /// <summary>
        /// Real-time count of cards remaining in the Extra Deck.
        /// Filters out invalid placeholders (-1) and subtracts extra monsters
        /// already present on field, in graveyard, or banished.
        /// </summary>
        public static int GetFilteredRegisteredExtraDeckCount(int player)
        {
            if (player != 0 && player != 1) return 0;
            var inner = GetRegisteredDeckArray(player, 1);
            if (inner == null || inner.Length == 0) return 0;

            var remaining = GetRemainingExtraDeckIds(player, inner);
            return remaining.Count;
        }

        private static System.Collections.Generic.List<int> GetRemainingExtraDeckIds(
            int player, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> inner)
        {
            var consumedIds = new System.Collections.Generic.List<int>();
            try
            {
                var roots = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager?.cardRoots;
                if (roots != null)
                {
                    for (int i = 0; i < roots.Count; i++)
                    {
                        var root = roots[i];
                        var place = root?.toLocator?.cardPlace;
                        if (place != null && place.team == player && place.position != LocateExtra && place.position != LocateDeck)
                        {
                            int cid = root.cardId;
                            if (cid == 0) cid = root.cardPlane?.cardModel?.cardId ?? 0;
                            if (cid > 0 && cid < 100000) consumedIds.Add(cid);
                        }
                    }
                }
            }
            catch { }

            // Also check tracked graveyard cards
            try
            {
                var grave = _trackedGraveCardIds[player];
                if (grave != null)
                {
                    for (int i = 0; i < grave.Count; i++)
                    {
                        int gcid = grave[i];
                        if (gcid > 0 && gcid < 100000 && !consumedIds.Contains(gcid))
                            consumedIds.Add(gcid);
                    }
                }
            }
            catch { }

            var remaining = new System.Collections.Generic.List<int>();
            for (int i = 0; i < inner.Length; i++)
            {
                int id = inner[i];
                // Filter out unassigned/empty extra deck slots (-1 / invalid IDs)
                if (id <= 0 || id >= 100000) continue;

                if (consumedIds.Contains(id))
                {
                    consumedIds.Remove(id); // consume one summoned/sent instance
                }
                else
                {
                    remaining.Add(id);
                }
            }
            return remaining;
        }

        private static CardSnapshot? GetFilteredRegisteredExtraDeckCard(int player, int slot)
        {
            if (slot < 0) return null;
            var inner = GetRegisteredDeckArray(player, 1);
            if (inner == null || inner.Length == 0) return null;

            var remaining = GetRemainingExtraDeckIds(player, inner);
            if (slot < remaining.Count)
            {
                return BuildContentSnapshot(remaining[slot]);
            }

            return null;
        }

        private static CardSnapshot? GetRegisteredDeckCard(int player, int locate, int slot)
        {
            if (slot < 0) return null;
            int deckType = locate switch
            {
                LocateExtra => 1,
                LocateDeck => 0,
                _ => -1
            };
            if (deckType < 0) return null;
            var inner = GetRegisteredDeckArray(player, deckType);
            if (inner == null || slot >= inner.Length) return null;
            int cardId = inner[slot];
            return BuildContentSnapshot(cardId);
        }

        /// <summary>
        /// Count of cards in the registered deck array for the given stack
        /// zone. Returns 0 for opponents / non-stack locates / when the
        /// engine cache isn't ready. Used by GetCardCount as the primary
        /// source for the local player's extra deck and main deck in PvP.
        /// </summary>
        private static int GetRegisteredDeckCount(int player, int locate)
        {
            int deckType = locate switch
            {
                LocateExtra => 1,
                LocateDeck => 0,
                _ => -1
            };
            if (deckType < 0) return 0;
            var inner = GetRegisteredDeckArray(player, deckType);
            return inner?.Length ?? 0;
        }

        private static CardSnapshot MakeSnapshot(Il2CppYgomGame.Duel.CardRoot root,
            bool isFaceUp)
        {
            int cardId = 0, uniqueId = 0, atk = 0, def = 0, level = 0;
            bool isAttack = false;
            try { cardId = root.cardId; } catch { }
            try { uniqueId = root.uniqueId; } catch { }
            try { atk = root.atk; } catch { }
            try { def = root.def; } catch { }
            try { level = root.level; } catch { }
            try { isAttack = root.isAttack; } catch { }

            // Fall back to CardModel.cardId when the root reports 0. The visual
            // layer renders face-down stack cards (extra deck, deck) with
            // root.cardId=0 — but the underlying CardPlane/CardModel had to
            // load the actual card texture and still carries the real id.
            // This recovers names for extra-deck CardRoots in PvP where the
            // engine uid path is hollow.
            //
            // Privacy gate: only apply when the card is face-up (public info)
            // OR owned by the local player (you may see your own set cards'
            // identities). Without this gate the fallback would leak the
            // opponent's set monsters / face-down spells if the client
            // happens to know them (e.g., AI duels where the engine has full
            // visibility).
            if (cardId <= 0 || cardId >= 100000)
            {
                cardId = 0;
                bool localOwned = false;
                try
                {
                    var team = root.toLocator?.cardPlace?.team ?? -1;
                    localOwned = team >= 0 && team == MyPlayerNum();
                }
                catch { }

                if (isFaceUp || localOwned)
                {
                    try
                    {
                        var model = root.cardPlane?.cardModel;
                        if (model != null && model.cardId > 0 && model.cardId < 100000)
                            cardId = model.cardId;
                    }
                    catch { }
                }
            }

            if (cardId < 0 || cardId >= 100000) cardId = 0;

            return new CardSnapshot(uniqueId, cardId, ResolveCardName(cardId),
                atk, def, level, isAttack, isFaceUp);
        }

        /// <summary>
        /// True when the engine's card-state queries are empty even though
        /// the visual layer has data — the signal that this is a PvP duel
        /// (server runs the logic, local engine is a renderer). Compares
        /// engine hand count vs nearHandCard.m_InfoList.Count.
        /// </summary>
        public static bool IsEngineHollow
        {
            get
            {
                if (_isPvPEngineLocked) return true;
                try
                {
                    int my = MyPlayerNum();
                    int engineHand = 0;
                    try { engineHand = Il2CppYgomGame.Duel.Engine
                        .DLL_DuelGetCardNum(my, LocateHand); }
                    catch { }
                    if (engineHand > 0) return false;

                    var hand = GetHandCardsFor(my);
                    int visualHand = hand?.m_InfoList?.Count ?? 0;
                    if (visualHand > 0)
                    {
                        _isPvPEngineLocked = true;
                        return true;
                    }

                    // Fallback detection when hand is empty (used up in combo):
                    // In PvP, engine returns 0 for monster/spell zones even when cards are on field
                    var roots = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager?.cardRoots;
                    if (roots != null && roots.Count > 0)
                    {
                        int engineMonster = -1;
                        try { engineMonster = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardNum(my, 2); } catch { }
                        if (engineMonster == 0)
                        {
                            _isPvPEngineLocked = true;
                            return true;
                        }
                    }

                    return false;
                }
                catch { return false; }
            }
        }

        private static string ResolveCardName(int cardDbId)
        {
            if (cardDbId <= 0 || cardDbId > 100000) return null;
            try
            {
                var content = Il2CppYgomGame.Card.Content.Instance;
                if (content == null) return null;
                string name = content.GetName(cardDbId);
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch { return null; }
        }

        #endregion
    }
}
