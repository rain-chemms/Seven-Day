using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SevenDay.Core;

/// <summary>
/// 模型处理工具：Blender 导出的模型以「米」为单位，与六边形格子（外接半径 1）差一个数量级。
///
/// 摆放规则（重要）：
///  · XZ 用「底面（最矮那部分顶点）的包围盒中心」对齐格子中心，而不是整体 AABB 中心——
///    建筑上的旗杆、栏杆、炮管会把整体 AABB 拉偏，按它居中会让主体歪出格子。
///  · Y 让整体最低点贴到格子顶面。
///  · 缩放按整体 AABB 的宽度算，保证模型不超出格子。
/// </summary>
public static class ModelUtil
{
    /// <summary>底面采样带：低于「最低点 + 22% 高度」的顶点算作底面。</summary>
    private const float GroundBand = 0.22f;

    /// <summary>顶点抽样的上限，避免大模型卡顿。</summary>
    private const int MaxSamples = 4000;

    /// <summary>调试开关：打印模型包围盒尺寸。</summary>
    public static bool Verbose;

    private struct FitInfo
    {
        public Aabb Full;          // 模型空间整体包围盒
        public Vector2 GroundCenter; // 模型空间底面中心（XZ）
        public bool HasGround;

        /// <summary>模型空间的「屋顶高度」：顶点 Y 的 90 分位，忽略旗杆/天线这种细高零件。</summary>
        public float RoofY;
    }

    public static Aabb? CombinedAabb(Node3D model)
    {
        Aabb? total = null;
        foreach (MeshInstance3D mi in model.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>())
        {
            if (mi.Mesh == null || !mi.Visible)
                continue;

            Transform3D rel = mi.Transform;
            Aabb a = rel * mi.GetAabb();
            total = total == null ? a : total.Value.Merge(a);
        }

        if (total == null && model is MeshInstance3D single && single.Mesh != null && single.Visible)
            total = single.GetAabb();

        return total;
    }

    private static FitInfo Analyze(Node3D model, bool includeHidden = false, string[] exclude = null)
    {
        var info = new FitInfo();
        Aabb? total = null;
        var groundPts = new List<Vector2>();
        var allPts = new List<Vector3>();

        foreach (MeshInstance3D mi in model.FindChildren("*", "MeshInstance3D", true, false).Cast<MeshInstance3D>())
        {
            if (mi.Mesh == null || (!includeHidden && !mi.Visible))
                continue;

            // 被 hiddenParts 隐藏的部件（导弹、尾焰）不参与尺寸/落地计算，
            // 否则它们伸到地面以下的高度会把整座建筑顶起来悬空
            if (exclude != null && System.Array.IndexOf(exclude, mi.Name.ToString()) >= 0)
                continue;

            Transform3D rel = mi.Transform;
            Aabb a = rel * mi.GetAabb();
            total = total == null ? a : total.Value.Merge(a);

            Vector3[] faces = mi.Mesh.GetFaces();
            if (faces.Length == 0)
                continue;

            int step = Mathf.Max(1, faces.Length / MaxSamples);
            for (int i = 0; i < faces.Length; i += step)
                allPts.Add(rel * faces[i]);
        }

        if (total == null)
            return info;

        info.Full = total.Value;
        info.RoofY = b_top(total.Value);

        if (allPts.Count > 0)
        {
            float minY = allPts.Min(p => p.Y);
            float maxY = allPts.Max(p => p.Y);
            float bandTop = minY + (maxY - minY) * GroundBand;

            foreach (Vector3 p in allPts)
            {
                if (p.Y <= bandTop)
                    groundPts.Add(new Vector2(p.X, p.Z));
            }

            // 屋顶高度取 90 分位，避免旗杆、天线把「可站高度」拉高
            float[] ys = allPts.Select(p => p.Y).ToArray();
            System.Array.Sort(ys);
            info.RoofY = ys[Mathf.Clamp((int)(ys.Length * 0.9f), 0, ys.Length - 1)];

            // 中心：把顶点按高度分桶，取「下半部分里水平占地最大」的那一层的包围盒中心。
            // 这样旗杆、栏杆、炮管这些细长零件不会把中心拉偏，落点就是基座的中心。
            const int Bins = 16;
            float span = Mathf.Max(maxY - minY, 0.0001f);
            var binMin = new Vector2[Bins];
            var binMax = new Vector2[Bins];
            var binHas = new bool[Bins];

            foreach (Vector3 p in allPts)
            {
                int idx = Mathf.Clamp((int)((p.Y - minY) / span * Bins), 0, Bins - 1);
                if (!binHas[idx])
                {
                    binHas[idx] = true;
                    binMin[idx] = new Vector2(p.X, p.Z);
                    binMax[idx] = binMin[idx];
                }
                else
                {
                    binMin[idx] = new Vector2(Mathf.Min(binMin[idx].X, p.X), Mathf.Min(binMin[idx].Y, p.Z));
                    binMax[idx] = new Vector2(Mathf.Max(binMax[idx].X, p.X), Mathf.Max(binMax[idx].Y, p.Z));
                }
            }

            int best = -1;
            float bestArea = -1f;
            for (int i = 0; i < Bins; i++)
            {
                if (!binHas[i])
                    continue;
                // 只在模型下半部分找（基座一定在下面）
                float y = minY + (i + 0.5f) * span / Bins;
                if (y > minY + span * 0.6f)
                    continue;

                float area = (binMax[i].X - binMin[i].X) * (binMax[i].Y - binMin[i].Y);
                if (area > bestArea)
                {
                    bestArea = area;
                    best = i;
                }
            }

            if (best >= 0 && bestArea > 0f)
            {
                info.GroundCenter = (binMin[best] + binMax[best]) * 0.5f;
                info.HasGround = true;
            }

            // 再用「抬离地面那部分」的顶点重心微调一次：
            // 楼体本身顶点最密，重心稳稳落在建筑上；贴地的坡道、围栏拉不动它。
            // 宽底座的模型（比如 Lv2 指挥部一边有长平台）只靠底面分桶会偏，用重心压回来。
            float bodyTop = minY + (maxY - minY) * 0.20f;
            Vector2 sum = Vector2.Zero;
            int n = 0;
            foreach (Vector3 p in allPts)
            {
                if (p.Y < bodyTop)
                    continue;
                sum += new Vector2(p.X, p.Z);
                n++;
            }

            if (n > 32)
            {
                Vector2 centroid = sum / n;

                if (info.HasGround)
                {
                    // 两个估计各占一半，避免任何一边被极端形状带跑
                    info.GroundCenter = (info.GroundCenter + centroid) * 0.5f;
                }
                else
                {
                    info.GroundCenter = centroid;
                    info.HasGround = true;
                }
            }
        }

        if (!info.HasGround)
        {
            info.GroundCenter = new Vector2(
                info.Full.Position.X + info.Full.Size.X * 0.5f,
                info.Full.Position.Z + info.Full.Size.Z * 0.5f);
        }

        return info;

        static float b_top(Aabb a) => a.Position.Y + a.Size.Y;
    }

    /// <summary>
    /// 缩放、底面中心对齐原点、最低点贴 y=0。
    /// fitDiagonal = true 时按「水平对角线」缩放（建筑用，保证方形底座不会顶出六边形），
    /// 否则按最长边缩放（兵种用）。
    /// 返回缩放系数，height 返回缩放后的模型高度。
    /// </summary>
    public static float FitAndGround(Node3D model, float target, out float height, bool fitDiagonal = false,
        bool includeHidden = false, string[] exclude = null)
    {
        height = 0f;
        // 建筑的各级是「加法」，按**所有**网格（含当前等级隐藏的）来量尺寸，
        // 这样升级时整栋楼的缩放保持一致，不会每升一级就变一下大小。
        FitInfo info = Analyze(model, includeHidden, exclude);
        Aabb b = info.Full;

        Vector2 center = info.GroundCenter;
        Vector2 size = new(b.Size.X, b.Size.Z);

        if (Verbose)
            Godot.GD.Print($"[Model] full=({b.Size.X:F1},{b.Size.Z:F1})");

        float extent = fitDiagonal
            ? Mathf.Sqrt(size.X * size.X + size.Y * size.Y)
            : Mathf.Max(size.X, size.Y);

        if (extent <= 0.0001f)
            return 1f;

        float scale = target / extent;
        model.Scale = Vector3.One * scale;
        model.Position = new Vector3(
            -center.X * scale,
            -b.Position.Y * scale,
            -center.Y * scale);

        // 可站立的「屋顶高度」用 90 分位，旗杆/天线不算
        float roof = info.RoofY - b.Position.Y;
        height = (roof > 0.01f ? roof : b.Size.Y) * scale;
        return scale;
    }

    /// <summary>
    /// 建筑模型里其实含三个等级：Lv1_Mesh / Lv2_Mesh / 本体网格（= Lv3）。
    /// 按等级只显示对应那一个，其余隐藏，升级时就等于换了模型。
    /// 炮口闪光、炮塔、导弹这类小部件（Flash/Flame/Missile/Barrel/Turret）始终显示。
    /// </summary>
    public static void SetBuildingLevel(Node3D model, int level, string[] hiddenParts = null)
    {        if (model == null)
            return;

        List<MeshInstance3D> meshes = model.FindChildren("*", "MeshInstance3D", true, false)
            .Cast<MeshInstance3D>().ToList();
        if (meshes.Count == 0)
            return;

        // 节点名和资源名都可能是 Lv1_Mesh / Lv2_Mesh，两个都看
        static string Key(MeshInstance3D mi) => mi.Name + "|" + (mi.Mesh?.ResourceName ?? "");

        // 先记下原来的父级链：炮管/炮塔这类「部件」常常挂在某一级的网格节点下面，
        // reparent 之后就看不出来了，必须提前记下来，否则会出现「炮管跟着高等级显示、底座没有」的穿帮。
        foreach (MeshInstance3D mi in meshes)
        {
            var chain = new List<string>();
            Node p = mi.GetParent();
            while (p != null && p != model)
            {
                chain.Add(p.Name);
                p = p.GetParent();
            }

            mi.SetMeta("origChain", string.Join("/", chain));
        }

        // 导出的层级里，Lv1/Lv2 常常是「Lv3 网格」的子节点；
        // 直接隐藏父网格会连子网格一起消失，所以先把所有网格提到同一层（保留世界变换）。
        foreach (MeshInstance3D mi in meshes.ToList())
        {
            if (mi.GetParent() != model)
            {
                Transform3D gt = mi.GlobalTransform;
                mi.Reparent(model, false);
                mi.GlobalTransform = gt;
            }
        }

        bool hasVariants = meshes.Any(m => Key(m).Contains("Lv1") || Key(m).Contains("Lv2"));
        if (!hasVariants)
            return;

        foreach (MeshInstance3D mi in meshes)
        {
            string chain = mi.GetMeta("origChain", "").AsString();
            string k = Key(mi) + "|" + chain;

            // 按《建筑的指南》：Lv0 是基础模型，Lv1/Lv2 是「在上一级基础上追加」的装饰，
            // 所以游戏等级 1/2/3 = 模型 Lv0/Lv1/Lv2，子物体按「达到即显示」累积。
            if (k.Contains("Lv2"))
                mi.Visible = level >= 3;
            else if (k.Contains("Lv1"))
                mi.Visible = level >= 2;
            else
                mi.Visible = true;   // 基础模型（以及挂在它下面的炮塔/炮管等部件）始终显示

            // 弹药类部件（导弹、尾焰）按配置隐藏，只留建筑本体
            if (mi.Visible && hiddenParts != null)
            {
                string nodeName = mi.Name.ToString();
                foreach (string part in hiddenParts)
                {
                    if (string.IsNullOrEmpty(part))
                        continue;
                    // 精确匹配节点名：用 Contains 会把 MissileSilo 这种主体也一起隐藏掉
                    if (nodeName == part)
                    {
                        mi.Visible = false;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// 实例化模型、按等级筛选、缩放居中贴地、套上 3渲2 材质。
    /// height 返回缩放后的模型高度（用来把站在建筑上的单位抬起来）。
    /// </summary>
    public static Node3D SpawnModel(string path, Node3D parent, float targetWidth, int buildingLevel,
        out float height, Color? outlineColor = null, string[] hiddenParts = null)
    {
        height = 0f;
        if (string.IsNullOrEmpty(path) || !ResourceLoader.Exists(path))
            return null;

        var scene = GD.Load<PackedScene>(path);
        if (scene == null)
            return null;

        Node inst = scene.Instantiate();
        if (inst is not Node3D n3)
        {
            inst.QueueFree();
            return null;
        }

        parent.AddChild(n3);

        // 按《建筑的指南》设置等级可见性（Lv0/Lv1/Lv2 逐级叠加），
        // 尺寸则按「所有网格」统一量，升级时不会忽大忽小。
        if (buildingLevel > 0)
            SetBuildingLevel(n3, Mathf.Clamp(buildingLevel, 1, 3), hiddenParts);

        float scale = FitAndGround(n3, targetWidth, out height,
            fitDiagonal: buildingLevel > 0, includeHidden: buildingLevel > 0,
            exclude: hiddenParts);
        ToonMaterial.Apply(n3, path, scale, outlineColor);
        return n3;
    }

    public static Node3D SpawnModel(string path, Node3D parent, float targetWidth, int buildingLevel = 0) =>
        SpawnModel(path, parent, targetWidth, buildingLevel, out _);
}
