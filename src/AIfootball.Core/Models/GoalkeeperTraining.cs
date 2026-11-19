using System.Text.Json.Serialization;

namespace AIfootball.Core.Models;

/// <summary>
/// 一条门将训练标注：某次射门的门线落点 + 是否被扑出。
/// 落点用射手视角（负 = 左侧），单位为米，与 <see cref="GoalGeometry"/> 一致。
/// </summary>
public sealed record KeeperShotSample(
    string Clip,
    double Lateral,
    double Height,
    bool Saved,
    double? Speed = null,
    string? Note = null)
{
    /// <summary>命中区域键（Lo wLeft … HighRight），与 Unity ProbabilityMap 同名。</summary>
    public string ZoneKey => GoalGeometry.ZoneKey(Lateral, Height);

    /// <summary>是否落在门框内；偏出的射门不参与扑救率统计。</summary>
    public bool OnTarget => Math.Abs(Lateral) <= GoalGeometry.HalfWidth
                            && Height >= 0
                            && Height <= GoalGeometry.Height;
}

/// <summary>训练后单个区域的统计与估计值。</summary>
public sealed record ZoneTrainingStat(
    int Row,
    int Column,
    string Key,
    int Samples,
    int Saves,
    double Probability)
{
    /// <summary>样本内的原始扑救率；无样本时为 null。</summary>
    public double? RawRate => Samples > 0 ? (double)Saves / Samples : null;
}

/// <summary>训练结果的置信度分级，用于界面提示而不是拒绝训练。</summary>
public enum TrainingConfidence
{
    Insufficient,
    Low,
    Medium,
    High,
}

public static class TrainingConfidenceText
{
    public static string Describe(this TrainingConfidence confidence) => confidence switch
    {
        TrainingConfidence.High => "高（样本充足）",
        TrainingConfidence.Medium => "中",
        TrainingConfidence.Low => "低（建议继续标注）",
        _ => "不足（先用先验值占位）",
    };
}

/// <summary>
/// 一次门将训练的结果。既包含可直接落盘的 <see cref="GoalkeeperProfile"/>（Unity 契约），
/// 也包含供界面展示与审计的逐区域样本数、置信度和诊断指标。
/// </summary>
public sealed class GoalkeeperTrainingResult
{
    public required GoalkeeperProfile Profile { get; init; }
    public required IReadOnlyList<ZoneTrainingStat> Zones { get; init; }
    public required int ShotCount { get; init; }
    public required int SaveCount { get; init; }
    public required int OffTargetCount { get; init; }
    public required double GlobalSaveRate { get; init; }
    public required TrainingConfidence Confidence { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>数据里体现的左右倾向（-1 左 … 1 右），仅用于展示。</summary>
    public required double SideIndex { get; init; }

    /// <summary>数据里体现的高低倾向（-1 低 … 1 高），仅用于展示。</summary>
    public required double HeightIndex { get; init; }

    /// <summary>由总扑救率反推的综合能力（仅展示，不写进判定）。</summary>
    public required double EstimatedGoalKeeping { get; init; }

    /// <summary>K 折交叉验证质量，用于判断这一次重训是否真的比上一代更好。</summary>
    public TrainingQuality Quality { get; init; } = TrainingQuality.Empty;

    /// <summary>逐区域原始扑救率（0-1），供热图叠加观察。</summary>
    public double[] ZoneProbabilities() => Zones.Select(zone => zone.Probability).ToArray();

    /// <summary>逐区域样本数，供界面标注"这一格其实没数据"。</summary>
    public int[] ZoneSamples() => Zones.Select(zone => zone.Samples).ToArray();
}

/// <summary>
/// 落盘的训练模型文件（data/goalkeepers/models/&lt;name&gt;.model.json）。
/// 与 <see cref="GoalkeeperProfile"/> 分开保存：Profile 是 Unity 能吃的纯判定数据，
/// 模型文件额外带训练血统，便于复现和审计。
/// </summary>
public sealed class GoalkeeperModelDocument
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("SchemaVersion")] public int SchemaVersion { get; set; } = CurrentVersion;

    /// <summary>训练代数：每追加标注并重训一次就 +1。</summary>
    [JsonPropertyName("Version")] public int Version { get; set; } = 1;

    [JsonPropertyName("TrainedAt")] public string TrainedAt { get; set; } = "";
    [JsonPropertyName("Source")] public string Source { get; set; } = "";
    [JsonPropertyName("ShotCount")] public int ShotCount { get; set; }
    [JsonPropertyName("SaveCount")] public int SaveCount { get; set; }
    [JsonPropertyName("OffTargetCount")] public int OffTargetCount { get; set; }
    [JsonPropertyName("GlobalSaveRate")] public double GlobalSaveRate { get; set; }
    [JsonPropertyName("Confidence")] public string Confidence { get; set; } = "";
    [JsonPropertyName("SideIndex")] public double SideIndex { get; set; }
    [JsonPropertyName("HeightIndex")] public double HeightIndex { get; set; }
    [JsonPropertyName("ZoneSamples")] public int[] ZoneSamples { get; set; } = [];
    [JsonPropertyName("ZoneSaves")] public int[] ZoneSaves { get; set; } = [];
    [JsonPropertyName("Warnings")] public string[] Warnings { get; set; } = [];
    [JsonPropertyName("Quality")] public TrainingQualityDocument Quality { get; set; } = new();
    [JsonPropertyName("Profile")] public GoalkeeperProfile Profile { get; set; } = new();

    /// <summary>历代训练记录，构成门将的"进化史"。</summary>
    [JsonPropertyName("History")] public List<GoalkeeperTrainingHistoryEntry> History { get; set; } = [];
}

/// <summary>一次训练的存档条目，用来画出进化曲线。</summary>
public sealed class GoalkeeperTrainingHistoryEntry
{
    [JsonPropertyName("Version")] public int Version { get; set; }
    [JsonPropertyName("TrainedAt")] public string TrainedAt { get; set; } = "";
    [JsonPropertyName("ShotCount")] public int ShotCount { get; set; }
    [JsonPropertyName("SaveCount")] public int SaveCount { get; set; }
    [JsonPropertyName("GlobalSaveRate")] public double GlobalSaveRate { get; set; }
    [JsonPropertyName("Confidence")] public string Confidence { get; set; } = "";
    [JsonPropertyName("CrossValidationLogLoss")] public double CrossValidationLogLoss { get; set; }
    [JsonPropertyName("Source")] public string Source { get; set; } = "";
}

/// <summary>模型质量的交叉验证结论，用于判断"这一次重训到底有没有变好"。</summary>
public sealed class TrainingQualityDocument
{
    [JsonPropertyName("Folds")] public int Folds { get; set; }
    [JsonPropertyName("Samples")] public int Samples { get; set; }
    [JsonPropertyName("LogLoss")] public double LogLoss { get; set; }
    [JsonPropertyName("Brier")] public double Brier { get; set; }
    [JsonPropertyName("BaselineLogLoss")] public double BaselineLogLoss { get; set; }
}

/// <summary>交叉验证结果（K 折，用于估计模型泛化能力）。</summary>
public sealed record TrainingQuality(
    int Folds,
    int Samples,
    double LogLoss,
    double Brier,
    double BaselineLogLoss)
{
    public static TrainingQuality Empty { get; } = new(0, 0, 0, 0, 0);

    public bool HasData => Samples > 0;

    /// <summary>相对"永远猜全局均值"的基线提升（正数表示模型更好）。</summary>
    public double Improvement => BaselineLogLoss <= 0 ? 0 : (BaselineLogLoss - LogLoss) / BaselineLogLoss;
}

/// <summary>
/// 单机位视频的门框标定：图像里四个门角点 + 视频原始尺寸。
/// 四个点顺序固定为 左上、右上、右下、左下（<b>画面</b>方向，不是射手视角）。
/// </summary>
public sealed class GoalFrameCalibration
{
    [JsonPropertyName("VideoWidth")] public int VideoWidth { get; set; }
    [JsonPropertyName("VideoHeight")] public int VideoHeight { get; set; }
    [JsonPropertyName("LeftTopX")] public double LeftTopX { get; set; }
    [JsonPropertyName("LeftTopY")] public double LeftTopY { get; set; }
    [JsonPropertyName("RightTopX")] public double RightTopX { get; set; }
    [JsonPropertyName("RightTopY")] public double RightTopY { get; set; }
    [JsonPropertyName("RightBottomX")] public double RightBottomX { get; set; }
    [JsonPropertyName("RightBottomY")] public double RightBottomY { get; set; }
    [JsonPropertyName("LeftBottomX")] public double LeftBottomX { get; set; }
    [JsonPropertyName("LeftBottomY")] public double LeftBottomY { get; set; }

    /// <summary>
    /// 点这四个门角时停在**哪一帧**。
    ///
    /// 标定是对一帧画的：画面停在哪一帧，四个点就属于那一帧。
    /// 之前没记这个字段，于是"这一脚"的橙框被画到这一脚的每一帧上——
    /// 用户看到的是满屏橙框，还以为是标定跑到别的脚上去了。
    /// -1 表示老数据（2026-10-24 之前存的），没有帧号可用。
    /// </summary>
    [JsonPropertyName("Frame")] public int Frame { get; set; } = -1;

    /// <summary>点这四个门角时的播放头秒数；-1 表示老数据。比帧号更精确，判"是不是这一帧"用它。</summary>
    [JsonPropertyName("FrameSeconds")] public double FrameSeconds { get; set; } = -1;

    /// <summary>老数据没有帧号，只能靠"是不是正在标这一脚"来决定要不要画。</summary>
    [JsonIgnore] public bool HasFrame => Frame >= 0 && FrameSeconds >= 0;

    [JsonIgnore]
    public bool IsComplete => VideoWidth > 0 && VideoHeight > 0
                              && (LeftTopX != RightTopX || LeftTopY != RightTopY)
                              && (LeftBottomX != RightBottomX || LeftBottomY != RightBottomY);

    public (double X, double Y)[] ImagePoints() =>
    [
        (LeftTopX, LeftTopY),
        (RightTopX, RightTopY),
        (RightBottomX, RightBottomY),
        (LeftBottomX, LeftBottomY),
    ];

    /// <summary>
    /// 画面四点 → 门平面四角（射手视角）。画面左对应射手视角左时用这个顺序；
    /// 单机位通常在射手身后，画面左右与射手视角一致。
    /// </summary>
    public static (double X, double Y)[] GoalPoints() =>
    [
        (-GoalGeometry.HalfWidth, GoalGeometry.Height),
        (GoalGeometry.HalfWidth, GoalGeometry.Height),
        (GoalGeometry.HalfWidth, 0),
        (-GoalGeometry.HalfWidth, 0),
    ];
}

/// <summary>一个视频片段的标注结果（data/goalkeeper_training/&lt;keeper&gt;/&lt;clip&gt;/annotation.json）。</summary>
public sealed class KeeperClipAnnotation
{
    [JsonPropertyName("Clip")] public string Clip { get; set; } = "";
    [JsonPropertyName("MarkedAt")] public string MarkedAt { get; set; } = "";
    [JsonPropertyName("Lateral")] public double Lateral { get; set; }
    [JsonPropertyName("Height")] public double Height { get; set; }
    [JsonPropertyName("Saved")] public bool Saved { get; set; }
    [JsonPropertyName("VideoSeconds")] public double VideoSeconds { get; set; }
    [JsonPropertyName("Note")] public string? Note { get; set; }

    /// <summary>用户点的球心在画面里的像素坐标。</summary>
    [JsonPropertyName("BallX")] public double? BallX { get; set; }

    /// <summary>用户点的球心在画面里的像素坐标。</summary>
    [JsonPropertyName("BallY")] public double? BallY { get; set; }

    /// <summary>标这一脚时播放头所在的帧号（25fps 估算），便于回看同一帧。</summary>
    [JsonPropertyName("FrameIndex")] public int? FrameIndex { get; set; }

    /// <summary>球心相对门线的像素距离；正值＝还没过线，负值＝已经过线。</summary>
    [JsonPropertyName("BelowLinePx")] public double? BelowLinePx { get; set; }

    /// <summary>标记这一脚时画面上叠加的门框矩形快照。</summary>
    [JsonPropertyName("GoalRect")] public GoalRect? GoalRect { get; set; }

    /// <summary>门框来源：precomputed（预扫描命中）/ detect（单帧即时检测）/ manual（用户手点）。</summary>
    [JsonPropertyName("GoalRectSource")] public string? GoalRectSource { get; set; }

    /// <summary>
    /// 标注这一脚时用的门框四点快照。同一段合集里不同比赛的机位不同，
    /// 只保留"当前标定"会让历史标注无法复算落点；留快照就能追溯每一条用的哪套门框。
    /// null 表示沿用门将目录里的 calibration.json。
    /// </summary>
    [JsonPropertyName("Calibration")] public GoalFrameCalibration? Calibration { get; set; }

    public string ZoneKey => GoalGeometry.ZoneKey(Lateral, Height);

    public KeeperShotSample ToSample() => new(Clip, Lateral, Height, Saved, null, Note);
}
