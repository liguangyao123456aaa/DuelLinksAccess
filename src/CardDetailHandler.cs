using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using Il2CppYgomGame;
using Il2CppYgomGame.Menu;
using Il2CppYgomGame.Card;

namespace DuelLinksAccess
{
    /// <summary>
    /// Full keyboard accessibility handler for the CardDetail popup screen.
    /// Manages card text readout, related buttons (Source, Relation, Favorite, Deck Recipe),
    /// and dependable escape/back dismissal back to the underlying screen (Shop/Trader/Deck).
    /// </summary>
    public class CardDetailHandler
    {
        #region Types

        private class DetailAction
        {
            public string Label;
            public Action Execute;
        }

        #endregion

        #region Fields

        private CardDetailViewController _vc;
        private bool _wasActive;
        private int _lastMrk = -1;
        private int _focusIndex = 0;
        private readonly List<DetailAction> _actions = new();
        private float _cooldown;
        private const float CooldownTime = 0.3f;

        #endregion

        #region Properties

        /// <summary>
        /// Whether this handler is actively managing an open CardDetail popup.
        /// </summary>
        public bool IsActive => GameStateTracker.CurrentScreen == GameScreen.CardDetail;

        #endregion

        #region Public Methods

        public void Update()
        {
            if (_cooldown > 0f)
                _cooldown -= Time.deltaTime;

            if (!IsActive)
            {
                if (_wasActive) Deactivate();
                return;
            }

            var vc = TryGetCardDetailVC();
            if (vc == null)
            {
                if (_wasActive) Deactivate();
                return;
            }

            int currentMrk = GetCurrentMrk(vc);
            if (!_wasActive || currentMrk != _lastMrk)
            {
                Activate(vc, currentMrk);
            }

            ProcessInput();
        }

        #endregion

        #region Lifecycle

        private void Activate(CardDetailViewController vc, int mrk)
        {
            _vc = vc;
            _wasActive = true;
            _lastMrk = mrk;
            _focusIndex = 0;

            RefreshActions();

            string compact = mrk > 0 ? CardFormatter.FormatCompact(mrk) : "未知卡片";
            int possessed = -1;
            try { possessed = Il2CppYgomGame.Single.CardTraderInfoBase.CardPoss(mrk, 0); } catch { }

            string announcement = $"卡片详情：{compact}";
            if (possessed >= 0)
                announcement += $"，持有 {possessed} 张";
            announcement += "。按 V 读卡片效果，上下箭头浏览操作，Esc 关闭返回。";

            ScreenReader.Say(announcement);
            MelonLogger.Msg($"[CardDetail] Activated: mrk={mrk}, card='{compact}', actions={_actions.Count}");
        }

        private void Deactivate()
        {
            _wasActive = false;
            _vc = null;
            _lastMrk = -1;
            _actions.Clear();
            _focusIndex = 0;
            MelonLogger.Msg("[CardDetail] Deactivated");
        }

        #endregion

        #region VC Access

        private static CardDetailViewController TryGetCardDetailVC()
        {
            try
            {
                var mgr = CardDetailViewControllerManager.Instance;
                if (mgr != null)
                {
                    var vc = mgr.GetCardDetailVC();
                    if (vc != null && vc.gameObject != null && vc.gameObject.activeInHierarchy)
                        return vc;
                }
            }
            catch { }

            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedMgr != null && namedMgr.TryGetValue("carddetail", out var cdMgr) && cdMgr != null)
                {
                    var top = cdMgr.GetStackTopViewController();
                    var vc = top?.TryCast<CardDetailViewController>();
                    if (vc != null && vc.gameObject != null && vc.gameObject.activeInHierarchy)
                        return vc;
                }
            }
            catch { }

            return null;
        }

        private static int GetCurrentMrk(CardDetailViewController vc)
        {
            if (vc == null) return -1;
            try
            {
                int mrk = vc.GetMrk();
                if (mrk > 0) return mrk;

                var card = vc.GetCurrentCard();
                if (card.Mrk > 0) return card.Mrk;
            }
            catch { }
            return -1;
        }

        #endregion

        #region Actions

        private void RefreshActions()
        {
            _actions.Clear();
            if (_vc == null) return;

            int mrk = _lastMrk;

            // 1. Read card full text
            _actions.Add(new DetailAction
            {
                Label = "读卡片效果",
                Execute = ReadCardDescription
            });

            // 2. Card Source button
            bool hasSrc = false;
            try { hasSrc = _vc.m_CardSrcButton != null && _vc.m_CardSrcButton.gameObject.activeInHierarchy; } catch { }
            if (hasSrc)
            {
                _actions.Add(new DetailAction
                {
                    Label = "获取途径",
                    Execute = () =>
                    {
                        try
                        {
                            MelonLogger.Msg("[CardDetail] Activating CardSrcButton");
                            _vc.OnClickCardSrcButton();
                            ScreenReader.Say("获取途径");
                            if (_vc.m_CardSrcButton != null && Main.TryGetUiScreenPos(_vc.m_CardSrcButton.gameObject, out var p))
                                Main.ClickViaHardwareMouse(p, "CardDetail-Src");
                        }
                        catch (Exception ex) { MelonLogger.Msg($"[CardDetail] CardSrc error: {ex.Message}"); }
                    }
                });
            }

            // 3. Related cards button
            bool hasRelation = false;
            try { hasRelation = _vc.m_RelationButton != null && _vc.m_RelationButton.gameObject.activeInHierarchy; } catch { }
            if (hasRelation)
            {
                _actions.Add(new DetailAction
                {
                    Label = "关联卡片",
                    Execute = () =>
                    {
                        try
                        {
                            MelonLogger.Msg("[CardDetail] Activating RelationButton");
                            _vc.OnClickRelationButton();
                            ScreenReader.Say("关联卡片");
                            if (_vc.m_RelationButton != null && Main.TryGetUiScreenPos(_vc.m_RelationButton.gameObject, out var p))
                                Main.ClickViaHardwareMouse(p, "CardDetail-Relation");
                        }
                        catch (Exception ex) { MelonLogger.Msg($"[CardDetail] Relation error: {ex.Message}"); }
                    }
                });
            }

            // 4. Favorite card toggle
            bool hasFav = false;
            try { hasFav = _vc.m_FavoriteButton != null && _vc.m_FavoriteButton.gameObject.activeInHierarchy; } catch { }
            if (hasFav)
            {
                bool isFav = false;
                try
                {
                    if (_vc.IsFavoriteButtonOn != null)
                        isFav = _vc.IsFavoriteButtonOn.Invoke(mrk);
                    else
                    {
                        var toggle = _vc.m_FavoriteButton.GetComponent<Toggle>();
                        if (toggle != null) isFav = toggle.isOn;
                    }
                }
                catch { }

                _actions.Add(new DetailAction
                {
                    Label = isFav ? "取消收藏" : "收藏卡片",
                    Execute = () =>
                    {
                        try
                        {
                            bool currentFav = false;
                            try
                            {
                                if (_vc.IsFavoriteButtonOn != null)
                                    currentFav = _vc.IsFavoriteButtonOn.Invoke(mrk);
                                else
                                {
                                    var toggle = _vc.m_FavoriteButton.GetComponent<Toggle>();
                                    if (toggle != null) currentFav = toggle.isOn;
                                }
                            }
                            catch { }

                            bool nextFav = !currentFav;
                            _vc.OnClickFavoriteButton(nextFav);
                            ScreenReader.Say(nextFav ? "已收藏" : "已取消收藏");
                            MelonLogger.Msg($"[CardDetail] Favorite toggled to {nextFav}");
                            RefreshActions();
                        }
                        catch (Exception ex) { MelonLogger.Msg($"[CardDetail] Favorite error: {ex.Message}"); }
                    }
                });
            }

            // 5. Deck Recipe button
            bool hasRecipe = false;
            try { hasRecipe = _vc.m_DeckRecipeButton != null && _vc.m_DeckRecipeButton.gameObject.activeInHierarchy; } catch { }
            if (hasRecipe)
            {
                _actions.Add(new DetailAction
                {
                    Label = "推荐卡组",
                    Execute = () =>
                    {
                        try
                        {
                            MelonLogger.Msg("[CardDetail] Activating DeckRecipeButton");
                            _vc.OnClickDeckRecipeButton();
                            ScreenReader.Say("推荐卡组");
                            if (_vc.m_DeckRecipeButton != null && Main.TryGetUiScreenPos(_vc.m_DeckRecipeButton.gameObject, out var p))
                                Main.ClickViaHardwareMouse(p, "CardDetail-Recipe");
                        }
                        catch (Exception ex) { MelonLogger.Msg($"[CardDetail] Recipe error: {ex.Message}"); }
                    }
                });
            }

            // 6. Possession Detail button
            bool hasPoss = false;
            try { hasPoss = _vc.m_PossDetailButton != null && _vc.m_PossDetailButton.gameObject.activeInHierarchy; } catch { }
            if (hasPoss)
            {
                _actions.Add(new DetailAction
                {
                    Label = "持有详情",
                    Execute = () =>
                    {
                        try
                        {
                            MelonLogger.Msg("[CardDetail] Activating PossDetailButton");
                            _vc.OnClickPossDetailButton();
                            ScreenReader.Say("持有详情");
                            if (_vc.m_PossDetailButton != null && Main.TryGetUiScreenPos(_vc.m_PossDetailButton.gameObject, out var p))
                                Main.ClickViaHardwareMouse(p, "CardDetail-Poss");
                        }
                        catch (Exception ex) { MelonLogger.Msg($"[CardDetail] Poss error: {ex.Message}"); }
                    }
                });
            }

            // 7. Close button
            _actions.Add(new DetailAction
            {
                Label = "关闭卡片详情",
                Execute = CloseCardDetail
            });
        }

        private void ReadCardDescription()
        {
            if (_lastMrk <= 0)
            {
                string rawText = _vc?.m_CardText?.text;
                if (!string.IsNullOrEmpty(rawText))
                {
                    ScreenReader.Say(LabelExtractor.StripRichText(rawText));
                    return;
                }
                ScreenReader.Say("无可用卡片效果说明");
                return;
            }

            string verbose = CardFormatter.FormatVerbose(_lastMrk);
            ScreenReader.Say(verbose);
        }

        private void CloseCardDetail()
        {
            try
            {
                MelonLogger.Msg("[CardDetail] Closing CardDetail popup");

                // Strategy 1: Clear / Disp(false) via API
                try { _vc?.Clear(); } catch { }

                // Strategy 2: Simulate mouse click on background mask or screen edge (20, 20)
                if (_vc?.m_MaskImage != null && Main.TryGetUiScreenPos(_vc.m_MaskImage.gameObject, out var maskPos))
                {
                    Main.ClickViaHardwareMouse(maskPos, "CardDetail-MaskClose");
                }
                else
                {
                    Main.ClickViaHardwareMouse(new Vector2(20f, 20f), "CardDetail-CornerClose");
                }

                ScreenReader.Say("已关闭卡片详情");

                // Immediately force state re-evaluation so underlying Shop/Trader regains focus
                GameStateTracker.ForceReevaluate();
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[CardDetail] Close error: {ex.Message}");
                ScreenReader.Say("关闭失败");
            }
        }

        #endregion

        #region Input

        private void ProcessInput()
        {
            if (InputManager.TryConsumeKeyDown(KeyCode.Escape)
                || InputManager.TryConsumeKeyDown(KeyCode.Backspace))
            {
                CloseCardDetail();
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.V)
                || InputManager.TryConsumeKeyDown(KeyCode.C)
                || InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                ReadCardDescription();
                return;
            }

            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.UpArrow)
                || InputManager.TryConsumeKeyDownOrRepeat(KeyCode.LeftArrow))
            {
                Navigate(-1);
                return;
            }

            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.DownArrow)
                || InputManager.TryConsumeKeyDownOrRepeat(KeyCode.RightArrow))
            {
                Navigate(1);
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.Return)
                || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter)
                || InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
                ExecuteCurrentAction();
                return;
            }
        }

        private void Navigate(int delta)
        {
            if (_actions.Count == 0) return;

            _focusIndex = (_focusIndex + delta + _actions.Count) % _actions.Count;
            AnnounceCurrentAction();
        }

        private void AnnounceCurrentAction()
        {
            if (_focusIndex >= 0 && _focusIndex < _actions.Count)
            {
                string text = $"{_actions[_focusIndex].Label}，第 {_focusIndex + 1} 项，共 {_actions.Count} 项";
                ScreenReader.Say(text);
            }
        }

        private void ExecuteCurrentAction()
        {
            if (_cooldown > 0f) return;
            _cooldown = CooldownTime;

            if (_focusIndex >= 0 && _focusIndex < _actions.Count)
            {
                _actions[_focusIndex].Execute?.Invoke();
            }
        }

        #endregion
    }
}
