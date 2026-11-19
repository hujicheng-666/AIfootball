using System.Text.Json.Serialization;

namespace AIfootball.Core.Models;

/// <summary>门框在画面里的中位矩形（像素）。</summary>
public sealed class GoalRect
{
    [JsonPropertyName("cx")] public double Cx { get; set; }
    [JsonPropertyName("cy")] public double Cy { get; set; }
    [JsonPropertyName("w")] public double W { get; set; }
    [JsonPropertyName("h")] public double H { get; set; }

    /// <summary>
    /// 四角精确坐标（顺时针：左上、右上、右下、左下）。单帧检测能给出带倾斜的四点，
    /// 预扫描的中位矩形只有轴对齐的框。倾斜的门框用包围盒会把门线压平，
    /// 左右端能差出几十像素，所以有四点就一定要用四点。
    /// </summary>
    [JsonPropertyName("quad")] public double[][]? Quad { get; set; }

    [JsonIgnore] public double Left => Cx - W / 2.0;
    [JsonIgnore] public double Right => Cx + W / 2.0;
    [JsonIgnore] public double Top => Cy - H / 2.0;

    /// <summary>球门线在画面里的 y：底边两端点的中点。这就是门柱与地面的交点连线。</summary>
    [JsonIgnore]
    public double GoalLineY
    {
        get
        {
            var c = Corners();
            return (c[2].Y + c[3].Y) / 2.0;
        }
    }

    /// <summary>太小的框多半是误检，当成没有。</summary>
    [JsonIgnore] public bool IsUsable => W >= 40 && H >= 20;

    /// <summary>四角（顺时针：左上、右上、右下、左下）。有精确四点就用四点，否则用矩形。</summary>
    public (double X, double Y)[] Corners()
    {
        if (Quad is { Length: 4 } q)
        {
            var points = new (double X, double Y)[4];
            for (var i = 0; i < 4; i++)
            {
                if (q[i] is not { Length: >= 2 } p) return FromBox();
                points[i] = (p[0], p[1]);
            }
            return points;
        }
        return FromBox();
    }

    private (double X, double Y)[] FromBox() =>
    [
        (Left, Top),
        (Right, Top),
        (Right, Cy + H / 2.0),
        (Left, Cy + H / 2.0),
    ];

    /// <summary>
    /// 球心相对门线的位置（像素）。相机在射手身后时，球飞向球门是往画面上方走，
    /// 所以正值＝球还在门线这一侧（没过线），负值＝球已经在球门里面。
    /// </summary>
    public double BelowLinePx(double ballX, double ballY) => ballY - GoalLineY;
}

/// <summary>
/// 一个可信门框检测段：在一段时间内门框在画面里的位置基本不变。
///
/// 由 tools/goal_detect 的逐帧扫描加抖动自检产出。用途是把这个矩形叠加到播放画面上，
/// 标注者就不必每次手点四个门角——点球合集里机位频繁切换，手标是主要的时间黑洞。
///
/// 可信的判据是段内逐帧 jitter（相邻帧差），不是段内标准差：镜头平滑摇移会让标准差
/// 很大而 jitter 很小，用标准差会误杀大量正确检测。
/// </summary>
public sealed class GoalSegmentRect
{
    /// <summary>段号，与扫描结果对齐。</summary>
    [JsonPropertyName("seg")] public int Seg { get; set; }

    /// <summary>段起始时间（秒）。</summary>
    [JsonPropertyName("t0")] public double T0 { get; set; }

    /// <summary>段结束时间（秒）。</summary>
    [JsonPropertyName("t1")] public double T1 { get; set; }

    /// <summary>段内总帧数。</summary>
    [JsonPropertyName("frames")] public int Frames { get; set; }

    /// <summary>段内成功检出目标框的帧数。</summary>
    [JsonPropertyName("found")] public int Found { get; set; }

    /// <summary>段内门框的中位矩形。</summary>
    [JsonPropertyName("median")] public GoalRect? Median { get; set; }

    /// <summary>是否通过抖动自检。</summary>
    [JsonPropertyName("trusted")] public bool Trusted { get; set; }

    /// <summary>时间 t 是否落在本段内。</summary>
    public bool Contains(double t) => t >= T0 && t <= T1;

    /// <summary>门框四角（画面像素，顺时针：左上、右上、右下、左下）。</summary>
    public (double X, double Y)[] Corners() =>
        Median is { } rect ? rect.Corners() : Array.Empty<(double, double)>();

    /// <summary>可用的门框矩形；太小或缺失返回 null。</summary>
    [JsonIgnore]
    public GoalRect? UsableRect => Median is { IsUsable: true } rect ? rect : null;

    /// <summary>本段门线在画面里的 y。</summary>
    [JsonIgnore]
    public double GoalLineY => Median?.GoalLineY ?? 0.0;

    /// <summary>
    /// 用检测到的门框矩形生成门框标定。矩形的四个角直接对应球门四角，
    /// 不必再让用户手点四针；有了它就能把"球的像素"换成球门平面上的落点。
    /// </summary>
    public GoalFrameCalibration? ToCalibration(int videoWidth, int videoHeight)
    {
        if (videoWidth <= 0 || videoHeight <= 0) return null;
        if (UsableRect is not { } rect) return null;
        var c = rect.Corners();
        return new GoalFrameCalibration
        {
            VideoWidth = videoWidth,
            VideoHeight = videoHeight,
            LeftTopX = c[0].X, LeftTopY = c[0].Y,
            RightTopX = c[1].X, RightTopY = c[1].Y,
            RightBottomX = c[2].X, RightBottomY = c[2].Y,
            LeftBottomX = c[3].X, LeftBottomY = c[3].Y,
        };
    }

    /// <summary>把一次单帧检测的结果包成段，好和预扫描结果走同一套逻辑。</summary>
    public static GoalSegmentRect FromRect(GoalRect rect, double t0, double t1) => new()
    {
        Seg = -1,
        T0 = t0,
        T1 = t1,
        Frames = 1,
        Found = 1,
        Median = rect,
        Trusted = true,
    };
}
