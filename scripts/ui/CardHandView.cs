using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SevenDay.Battle;
using SevenDay.Core;
using SevenDay.Data;

namespace SevenDay.UI;

/// <summary>
/// 扇形手牌。版式与出牌方式参考《杀戮尖塔》+ 设计草案/战斗UI参考-4：
///   · 手牌在底部排成弧形，越靠边越倾斜、越低
///   · 悬停时抬起并对齐
///   · 单击选中（选中后地图高亮合法格），再点卡牌取消
///   · 双击直接打出（不需要地块的卡）
///   · 按住向上拖出阈值 → 打出（不需要地块的卡）
///   · 按住拖到地图上松手 → 在该格打出（需要地块的卡）
///   · 左右拖动 → 交换手牌位置
/// </summary>
public partial class CardHandView : Control
{
    public event Action<int> Selected;
    public event Action<int> DoubleClicked;
    public event Action<int> DragPlayed;
    public event Action<int, Vector2> DroppedOnMap;
    public event Action<int, int> Reordered;

    private const float Spacing = 96f;
    private readonly System.Collections.Generic.List<PanelContainer> _orderBadges = new();
    private bool _orderHintEnabled;
    private bool _orderHintReady = true;

    private const float AnglePerCard = 5.5f;
    private const float EdgeDrop = 4f;
    private const float HoverLift = 58f;
    private const float SelectLift = 84f;
    private const float DragUpThreshold = 110f;
    private const float ReorderThreshold = 55f;

    private readonly List<CardView> _cards = new();
    private List<int> _pickableIndices;
    private IReadOnlyList<int> _pickSelection;
    private readonly Dictionary<int, Tween> _tweens = new();

    private BattleState _state;
    private int _selected = -1;
    private int _hovered = -1;
    private int _dragging = -1;
    private Vector2 _dragDelta;

    public CardHandView()
    {
        MouseFilter = MouseFilterEnum.Pass;
        ClipContents = false;
    }

    public void SetState(BattleState state) => _state = state;

    /// <summary>手牌多选：给选中下标加金色边框。</summary>
    public void SetPickSelection(IReadOnlyList<int> indices)
    {
        _pickSelection = indices;
        for (int i = 0; i < _cards.Count; i++)
        {
            bool on = indices != null && indices.Contains(i);
            _cards[i].SetChosen(on);
            // 已选牌离开手牌区域，暂存到临时选择区
            _cards[i].Visible = !on;
        }

        Relayout(true);
    }
    /// <summary>选牌模式：只允许这些下标可点（null = 全部可选）。</summary>
    public void SetPickableIndices(IReadOnlyList<int> indices)
    {
        _pickableIndices = indices == null ? null : new List<int>(indices);
        for (int i = 0; i < _cards.Count; i++)
        {
            if (i < _state?.Player.Hand.Count)
                _cards[i].SetPickable(_pickableIndices == null || _pickableIndices.Contains(i));
        }
    }
    public void SetSelected(int index)
    {
        // 选中另一张手牌时也给一声（和悬停同源，稍响一点）
        if (index >= 0 && index != _selected && index < _cards.Count)
            AudioManager.PlaySfx("res://gameAsset/audios/ui/cardBattle_hover.ogg", -6f);

        _selected = index;
        for (int i = 0; i < _cards.Count; i++)
            _cards[i].SetChosen(i == index);

        // 手牌多选模式下，选中金框由选牌逻辑接管
        if (_pickSelection != null)
        {
            for (int i = 0; i < _cards.Count; i++)
                _cards[i].SetChosen(_pickSelection.Contains(i));
        }

        Relayout(true);
    }

    public bool TryGetCardRect(int index, out Rect2 rect)
    {
        rect = default;
        if (index < 0 || index >= _cards.Count)
            return false;
        rect = _cards[index].GetGlobalRect();
        return true;
    }

    /// <summary>是否显示「手牌顺序」编号（对应设置里的开关）。</summary>
    public void SetOrderHintEnabled(bool enabled)
    {
        if (_orderHintEnabled == enabled)
            return;

        _orderHintEnabled = enabled;

        foreach (PanelContainer badge in _orderBadges)
            badge.Visible = enabled && _orderHintReady;

        if (enabled)
            Relayout(false);
    }

    /// <summary>抽牌/产卡动画播完之前先把编号藏起来。</summary>
    public void SetOrderHintReady(bool ready)
    {
        _orderHintReady = ready;

        foreach (PanelContainer badge in _orderBadges)
            badge.Visible = _orderHintEnabled && ready;
    }

    /// <summary>保证编号牌数量与手牌一致（多删少补）。</summary>
    private void SyncOrderBadges()
    {
        while (_orderBadges.Count > _cards.Count)
        {
            PanelContainer extra = _orderBadges[^1];
            _orderBadges.RemoveAt(_orderBadges.Count - 1);
            RemoveChild(extra);
            extra.QueueFree();
        }

        while (_orderBadges.Count < _cards.Count)
        {
            var badge = new PanelContainer
            {
                MouseFilter = MouseFilterEnum.Ignore,
                ZIndex = 20,
                Visible = _orderHintEnabled && _orderHintReady,
            };

            var style = new StyleBoxFlat
            {
                BgColor = new Color("F4E7C6"),
                BorderColor = new Color("3B2E16"),
                BorderWidthLeft = 2, BorderWidthTop = 2,
                BorderWidthRight = 2, BorderWidthBottom = 2,
                CornerRadiusTopLeft = 11, CornerRadiusTopRight = 11,
                CornerRadiusBottomLeft = 11, CornerRadiusBottomRight = 11,
                ContentMarginLeft = 0, ContentMarginRight = 0,
                ContentMarginTop = 0, ContentMarginBottom = 0,
            };
            badge.AddThemeStyleboxOverride("panel", style);
            badge.CustomMinimumSize = new Vector2(24, 24);

            var label = new Label
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            label.AddThemeFontSizeOverride("font_size", 15);
            label.AddThemeColorOverride("font_color", new Color("3B2E16"));
            badge.AddChild(label);

            AddChild(badge);
            _orderBadges.Add(badge);
        }
    }

    /// <summary>把手牌编号摆到每张卡的正上方。</summary>
    private void LayoutOrderBadges()
    {
        int rank = 0;
        for (int i = 0; i < _orderBadges.Count && i < _cards.Count; i++)
        {
            PanelContainer badge = _orderBadges[i];
            CardView card = _cards[i];
            badge.Visible = _orderHintEnabled && _orderHintReady && card.Visible;

            if (!badge.Visible)
                continue;

            if (badge.GetChildCount() > 0 && badge.GetChild(0) is Label label)
                label.Text = (rank + 1).ToString();
            rank++;
            Vector2 size = badge.GetCombinedMinimumSize();
            Vector2 center = card.Position + card.Size * 0.5f;
            badge.Position = new Vector2(center.X - size.X * 0.5f, card.Position.Y - size.Y - 4f);
            badge.MoveToFront();
        }
    }
    /// <summary>「选一张弃掉」阶段的提示：整排手牌轻轻抬一下再落回。</summary>
    public void PulseHint()
    {
        foreach (CardView card in _cards)
        {
            if (!card.Visible)
                continue;

            card.PivotOffset = card.Size * 0.5f;
            Tween tween = CreateTween();
            tween.TweenProperty(card, "scale", card.Scale * 1.05f, 0.12f)
                .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
            tween.TweenProperty(card, "scale", card.Scale, 0.18f)
                .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        }
    }

    /// <summary>临时隐藏某张卡（出牌飞行期间用），布局保持不变。</summary>
    public void SetCardVisible(int index, bool visible)
    {
        if (index < 0 || index >= _cards.Count)
            return;
        _cards[index].Visible = visible;
    }

    /// <summary>找手牌里第一张指定卡牌的索引（跳过 exclude 里的槽位，找不到返回 -1）。</summary>
    public int IndexOfCard(string cardId, ICollection<int> exclude = null)
    {
        for (int i = 0; i < _cards.Count; i++)
        {
            if (exclude != null && exclude.Contains(i))
                continue;
            if (_cards[i].Def != null && _cards[i].Def.Id == cardId)
                return i;
        }

        return -1;
    }

    /// <summary>取卡牌的屏幕矩形与扇形旋转角（烧卡特效要对齐）。</summary>
    public bool TryGetCardTransform(int index, out Rect2 rect, out float rotationDeg)
    {
        rect = default;
        rotationDeg = 0f;
        if (index < 0 || index >= _cards.Count)
            return false;

        rect = _cards[index].GetGlobalRect();
        rotationDeg = _cards[index].RotationDegrees;
        return true;
    }

    /// <summary>重建整手牌（张数或内容变化时调用）：尽量复用已有卡牌节点，位置平滑过渡。</summary>
    public void Refresh()
    {
        if (_state == null)
        {
            foreach (CardView c in _cards)
                c.QueueFree();
            _cards.Clear();
            return;
        }

        List<string> hand = _state.Player.Hand;

        // 按「卡牌 id + 第几次出现」把旧卡牌配对，能对上的直接复用（保留当前位置）
        var spare = new Dictionary<string, List<CardView>>();
        foreach (CardView c in _cards)
        {
            string key = c.Def?.Id ?? "";
            if (!spare.TryGetValue(key, out List<CardView> list))
            {
                list = new List<CardView>();
                spare[key] = list;
            }
            list.Add(c);
        }

        var next = new List<CardView>();

        for (int i = 0; i < hand.Count; i++)
        {
            CardDef def = GameDatabase.Card(hand[i]);
            if (def == null)
                continue;

            CardView card = null;
            if (spare.TryGetValue(def.Id, out List<CardView> pool) && pool.Count > 0)
            {
                card = pool[0];
                pool.RemoveAt(0);
            }

            if (card == null)
            {
                // 新牌：出现在最右侧的位置，随后由 Relayout 平滑挪过去
                card = new CardView();
                AddChild(card);
                int shownCost = _state.CostAt(_state.Player, i);
                card.Setup(def, i, _state.Player.Gold >= shownCost, shownCost);
                card.Position = BasePosition(Mathf.Max(0, next.Count - 1));
                WireCard(card);
            }
            else
            {
                // 复用：只更新数据与下标（Build 内部会直接返回，不会重复建节点）
                // 注意要恢复可见性：打出的那张可能被 SetCardVisible(false) 藏起来了，
                // 复用它当下一张卡的话会“凭空少一张”。
                card.Visible = true;
                card.Modulate = Colors.White;
                card.Scale = Vector2.One;
                int shownCost = _state.CostAt(_state.Player, i);
                card.Setup(def, i, _state.Player.Gold >= shownCost, shownCost);
            }

            next.Add(card);
            card.SetEthereal(_state.IsEtherealAt(i));
        card.SetProducedExhaust(_state.IsProducedExhaustAt(i));
            card.SetRetained(_state.HasRetainAt(_state.Player, i));
            card.SetPickable(_pickableIndices == null || _pickableIndices.Contains(i));
        }

        // 没被复用的旧卡（确实离开了手牌）销毁
        foreach (List<CardView> leftovers in spare.Values)
        {
            foreach (CardView c in leftovers)
            {
                _tweens.Remove(_cards.IndexOf(c));
                c.QueueFree();
            }
        }

        _cards.Clear();
        _cards.AddRange(next);
        _tweens.Clear();

        if (_selected >= _cards.Count)
            _selected = -1;

        Relayout(true);
    }

    /// <summary>给一张卡挂事件：索引在触发时按当前列表查，避免复用后闭包拿到旧下标。</summary>
    private void WireCard(CardView card)
    {
        card.Clicked += _ => Selected?.Invoke(_cards.IndexOf(card));
        card.DoubleClicked += _ => DoubleClicked?.Invoke(_cards.IndexOf(card));

        card.DragStarted += (_, pos) =>
        {
            int idx = _cards.IndexOf(card);
            if (idx < 0)
                return;
            _dragging = idx;
            FocusCard(idx);
        };

        card.Dragging += (_, delta) =>
        {
            int idx = _cards.IndexOf(card);
            if (idx < 0)
                return;
            _dragDelta = delta;
            card.Position = BasePosition(idx) + delta;
            card.ZIndex = 400;
        };

        card.DragEnded += (_, release, start) =>
        {
            int idx = _cards.IndexOf(card);
            if (idx >= 0)
                OnDragEnded(idx, release, start);
        };

        // 右键放大检视
        card.InspectRequested += idx =>
        {
            if (idx >= 0 && idx < _cards.Count)
                CardInspectOverlay.Show(_cards[idx].Def, _state != null && _state.IsEtherealAt(idx), _state?.CostAt(_state.Player, idx) ?? -1);
        };

        card.MouseEntered += () =>
        {
            int idx = _cards.IndexOf(card);
            if (idx < 0)
                return;

            // 悬停到一张新手牌：轻响一下
            if (_hovered != idx)
                AudioManager.PlaySfx("res://gameAsset/audios/ui/cardBattle_hover.ogg", -8f);

            SetHover(idx);
        };

        card.MouseExited += () =>
        {
            if (_hovered >= 0 && _hovered < _cards.Count && _cards[_hovered] == card)
                SetHover(-1);
        };
    }

    private void OnDragEnded(int index, Vector2 release, Vector2 start)
    {
        _dragging = -1;
        _dragDelta = Vector2.Zero;

        Vector2 delta = release - start;
        CardDef def = index < _cards.Count ? _cards[index].Def : null;
        bool needsTarget = def != null && def.KindEnum != CardKind.Global;
        bool overMap = release.Y < GlobalPosition.Y - 10f;

        // 1) 需要地块的卡：拖到地图上松手 → 在那一格打出
        if (needsTarget && overMap && delta.Length() > 24f)
        {
            ResetCard(index);
            DroppedOnMap?.Invoke(index, release);
            return;
        }

        // 2) 不需要地块的卡：向上拖出阈值 → 直接打出
        if (!needsTarget && delta.Y < -DragUpThreshold && Mathf.Abs(delta.X) < Mathf.Abs(delta.Y))
        {
            ResetCard(index);
            DragPlayed?.Invoke(index);
            return;
        }

        // 3) 左右拖动 → 交换手牌位置
        if (Mathf.Abs(delta.X) > ReorderThreshold && Mathf.Abs(delta.Y) < ReorderThreshold)
        {
            int target = Mathf.Clamp(index + Mathf.RoundToInt(delta.X / Spacing), 0, _cards.Count - 1);
            ResetCard(index);
            if (target != index)
                Reordered?.Invoke(index, target);
            return;
        }

        // 4) 其它情况：回位；几乎没动就当成一次点击
        ResetCard(index);
        if (delta.Length() < 10f)
            Selected?.Invoke(index);
    }

    private void ResetCard(int index)
    {
        if (index < 0 || index >= _cards.Count)
            return;
        _cards[index].ZIndex = 0;
        Relayout(true);
    }

    private void FocusCard(int index)
    {
        if (index < 0 || index >= _cards.Count)
            return;
        _cards[index].ZIndex = 300;
    }

    private Vector2 BasePosition(int index) => BasePositionRank(index, _cards.Count);

    private Vector2 BasePositionRank(int rank, int visibleCount)
    {
        int n = visibleCount;
        if (n == 0)
            return Vector2.Zero;

        float center = (n - 1) * 0.5f;
        float t = rank - center;
        float width = Size.X > 1f ? Size.X : 1280f;
        float cx = width * 0.5f;
        // 留出底部边距，避免旋转后卡角被屏幕裁掉
        float baseY = Size.Y - 22f;

        return new Vector2(
            cx + t * Spacing - CardView.CardWidth * 0.5f,
            baseY - CardView.CardHeight + Mathf.Abs(t) * EdgeDrop);
    }

    public void Relayout(bool animate)
    {
        int n = _cards.Count;
        SyncOrderBadges();

        if (n == 0)
            return;

        int visibleCount = 0;
        for (int i = 0; i < n; i++)
        {
            if (_cards[i].Visible)
                visibleCount++;
        }

        float center = (visibleCount - 1) * 0.5f;
        int rank = 0;

        for (int i = 0; i < n; i++)
        {
            CardView card = _cards[i];
            // 已选入临时区的牌不占手牌位置，也不显示编号
            if (!card.Visible)
            {
                if (i < _orderBadges.Count)
                    _orderBadges[i].Visible = false;
                continue;
            }

            bool isSelected = i == _selected;
            bool isHover = i == _hovered && _dragging < 0;

            Vector2 pos = BasePositionRank(rank, visibleCount);
            float angle = (rank - center) * AnglePerCard;
            float lift = 0f;
            float scale = 1f;

            if (isSelected)
            {
                lift = SelectLift;
                angle = 0f;
                scale = 1.12f;
            }
            else if (isHover)
            {
                lift = HoverLift;
                angle *= 0.25f;
                scale = 1.07f;
            }

            pos.Y -= lift;
            if (isSelected || isHover)
                card.ZIndex = isSelected ? 250 : 200;
            else if (_dragging != i)
                card.ZIndex = i;

            Animate(card, i, pos, Mathf.DegToRad(angle), scale, animate, rank);
            rank++;
        }
    }

    private void Animate(CardView card, int index, Vector2 pos, float rot, float scale, bool animate,
                         int displayRank = -1)
    {
        PanelContainer badge = index >= 0 && index < _orderBadges.Count ? _orderBadges[index] : null;
        Vector2 badgePos = default;
        if (badge != null)
        {
            if (badge.GetChildCount() > 0 && badge.GetChild(0) is Label lbl)
                lbl.Text = ((displayRank >= 0 ? displayRank : index) + 1).ToString();

            badge.Visible = _orderHintEnabled && _orderHintReady;
            Vector2 bsize = badge.GetCombinedMinimumSize();

            // 卡牌是绕轴心旋转的，编号要贴「旋转后的上边中点」，否则会越往右越偏
            Vector2 half = card.Size * 0.5f;
            Vector2 pivot = card.PivotOffset;
            Vector2 topLocal = (new Vector2(half.X, 0f) - pivot).Rotated(rot) * scale;
            Vector2 top = pos + pivot + topLocal;

            badgePos = new Vector2(top.X - bsize.X * 0.5f, top.Y - bsize.Y - 4f);
        }

        if (!animate)
        {
            card.Position = pos;
            card.Rotation = rot;
            card.Scale = Vector2.One * scale;

            if (badge != null)
            {
                badge.Position = badgePos;
                badge.MoveToFront();
            }

            return;
        }

        if (_tweens.TryGetValue(index, out Tween old) && old.IsValid())
            old.Kill();

        Tween tw = CreateTween();
        tw.SetParallel(true);
        tw.TweenProperty(card, "position", pos, 0.16f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(card, "rotation", rot, 0.16f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tw.TweenProperty(card, "scale", Vector2.One * scale, 0.16f)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);

        if (badge != null)
        {
            tw.TweenProperty(badge, "position", badgePos, 0.16f)
                .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            badge.MoveToFront();
        }

        _tweens[index] = tw;
    }

    public void SetHover(int index)
    {
        if (_hovered == index)
            return;
        _hovered = index;
        Relayout(true);
    }

    public int Hovered => _hovered;
    public IReadOnlyList<CardView> Cards => _cards;
}
