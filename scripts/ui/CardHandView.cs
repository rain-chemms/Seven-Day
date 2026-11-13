using System;
using System.Collections.Generic;
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
    private const float AnglePerCard = 5.5f;
    private const float EdgeDrop = 4f;
    private const float HoverLift = 58f;
    private const float SelectLift = 84f;
    private const float DragUpThreshold = 110f;
    private const float ReorderThreshold = 55f;

    private readonly List<CardView> _cards = new();
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

    public void SetSelected(int index)
    {
        _selected = index;
        for (int i = 0; i < _cards.Count; i++)
            _cards[i].SetChosen(i == index);
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
                card.Setup(def, i, _state.Player.Gold >= def.Cost);
                card.Position = BasePosition(Mathf.Max(0, next.Count - 1));
                WireCard(card);
            }
            else
            {
                // 复用：只更新数据与下标（Build 内部会直接返回，不会重复建节点）
                // 注意要恢复可见性：打出的那张可能被 SetCardVisible(false) 藏起来了，
                // 复用它当下一张卡的话会“凭空少一张”。
                card.Visible = true;
                card.Setup(def, i, _state.Player.Gold >= def.Cost);
            }

            next.Add(card);
            card.SetEthereal(_state.IsEtherealAt(i));
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

        card.MouseEntered += () =>
        {
            int idx = _cards.IndexOf(card);
            if (idx >= 0)
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

    private Vector2 BasePosition(int index)
    {
        int n = _cards.Count;
        if (n == 0)
            return Vector2.Zero;

        float center = (n - 1) * 0.5f;
        float t = index - center;
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
        if (n == 0)
            return;

        float center = (n - 1) * 0.5f;

        for (int i = 0; i < n; i++)
        {
            CardView card = _cards[i];
            bool isSelected = i == _selected;
            bool isHover = i == _hovered && _dragging < 0;

            Vector2 pos = BasePosition(i);
            float angle = (i - center) * AnglePerCard;
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

            Animate(card, i, pos, Mathf.DegToRad(angle), scale, animate);
        }
    }

    private void Animate(CardView card, int index, Vector2 pos, float rot, float scale, bool animate)
    {
        if (!animate)
        {
            card.Position = pos;
            card.Rotation = rot;
            card.Scale = Vector2.One * scale;
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
