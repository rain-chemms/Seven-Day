using System;
using System.Collections.Generic;
using Godot;

namespace SevenDay.Core;

/// <summary>
/// 尖顶（pointy-top）六边形坐标，使用轴向坐标 (q, r)。
/// 与 gameAsset/models/hexTile 的 hexTile 对齐：外接半径 1 单位，
/// 宽度 = sqrt(3) * R，高度 = 2 * R。
/// </summary>
public readonly struct HexCoord : IEquatable<HexCoord>
{
    public readonly int Q;
    public readonly int R;

    public HexCoord(int q, int r)
    {
        Q = q;
        R = r;
    }

    /// <summary>立方坐标的第三个分量，恒为 -Q-R。</summary>
    public int S => -Q - R;

    public static readonly HexCoord Zero = new(0, 0);

    /// <summary>六个相邻方向：东、东北、西北、西、西南、东南。</summary>
    public static readonly HexCoord[] Directions =
    {
        new(1, 0), new(1, -1), new(0, -1), new(-1, 0), new(-1, 1), new(0, 1)
    };

    public HexCoord Neighbor(int direction) =>
        new(Q + Directions[direction].Q, R + Directions[direction].R);

    public IEnumerable<HexCoord> Neighbors()
    {
        for (int i = 0; i < 6; i++)
            yield return Neighbor(i);
    }

    public static int Distance(HexCoord a, HexCoord b)
    {
        return (Math.Abs(a.Q - b.Q)
                + Math.Abs(a.Q + a.R - b.Q - b.R)
                + Math.Abs(a.R - b.R)) / 2;
    }

    /// <summary>以 center 为心、半径 radius 的一圈格子（radius=0 时只返回自己）。</summary>
    public static IEnumerable<HexCoord> Ring(HexCoord center, int radius)
    {
        if (radius <= 0)
        {
            yield return center;
            yield break;
        }

        HexCoord hex = new(center.Q + Directions[4].Q * radius, center.R + Directions[4].R * radius);
        for (int d = 0; d < 6; d++)
        {
            for (int i = 0; i < radius; i++)
            {
                yield return hex;
                hex = hex.Neighbor(d);
            }
        }
    }

    /// <summary>以 center 为心、半径内所有格子（含 center）。</summary>
    public static IEnumerable<HexCoord> Spiral(HexCoord center, int radius)
    {
        yield return center;
        for (int k = 1; k <= radius; k++)
        {
            foreach (HexCoord h in Ring(center, k))
                yield return h;
        }
    }

    public static HexCoord operator +(HexCoord a, HexCoord b) => new(a.Q + b.Q, a.R + b.R);
    public static HexCoord operator -(HexCoord a, HexCoord b) => new(a.Q - b.Q, a.R - b.R);
    public static bool operator ==(HexCoord a, HexCoord b) => a.Q == b.Q && a.R == b.R;
    public static bool operator !=(HexCoord a, HexCoord b) => !(a == b);

    public bool Equals(HexCoord other) => Q == other.Q && R == other.R;
    public override bool Equals(object obj) => obj is HexCoord other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Q, R);
    public override string ToString() => $"({Q},{R})";
}

/// <summary>六边形与世界坐标（XZ 平面）之间的换算。</summary>
public static class HexLayout
{
    /// <summary>外接半径。与 hexTile 模型一致。</summary>
    public const float Size = 1.0f;

    public const float Sqrt3 = 1.7320508f;

    /// <summary>相邻两格中心在世界空间的距离（纵向）。</summary>
    public const float RowHeight = Size * 1.5f;

    public static Vector3 ToWorld(HexCoord c, float y = 0f)
    {
        float x = Sqrt3 * Size * (c.Q + c.R / 2f);
        float z = RowHeight * c.R;
        return new Vector3(x, y, z);
    }

    public static HexCoord FromWorld(Vector3 world)
    {
        float q = (Sqrt3 / 3f * world.X - 1f / 3f * world.Z) / Size;
        float r = (2f / 3f * world.Z) / Size;
        return Round(q, r);
    }

    private static HexCoord Round(float q, float r)
    {
        float s = -q - r;
        int rq = Mathf.RoundToInt(q);
        int rr = Mathf.RoundToInt(r);
        int rs = Mathf.RoundToInt(s);

        float dq = Mathf.Abs(rq - q);
        float dr = Mathf.Abs(rr - r);
        float ds = Mathf.Abs(rs - s);

        if (dq > dr && dq > ds)
            rq = -rr - rs;
        else if (dr > ds)
            rr = -rq - rs;

        return new HexCoord(rq, rr);
    }

    /// <summary>六边形的六个角（顶点），用于画边框/高亮。</summary>
    public static Vector3[] Corners(float radius, float y = 0f)
    {
        Vector3[] pts = new Vector3[6];
        for (int i = 0; i < 6; i++)
        {
            float angle = Mathf.DegToRad(60f * i + 30f);
            pts[i] = new Vector3(Mathf.Cos(angle) * radius, y, Mathf.Sin(angle) * radius);
        }

        return pts;
    }
}
