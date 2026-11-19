using AIfootball.Core.Models;

namespace AIfootball.Core.Services;

/// <summary>
/// 从标注好的射门样本里训练门将的 3×5 区域扑救概率表。
///
/// 为什么不是"直接统计每格扑救率"：真实标注量通常在几十到几百条，
/// 摊到 15 个区域后多数格子只有个位数样本，直接算会出现"1 扑 1 出 = 100%"这种假数据，
/// 把门将判成神或判成空气。这里做两级收缩：
///
/// 1. 格内做 Beta-二项后验（Jeffreys 先验 + 全局扑救率），把少样本格拉回全局水平；
/// 2. 格间做高斯邻域收缩：本格样本越少，越依赖相邻区域的估计，因为
///    "左下角"和"左中"本来就该相关，孤立的尖峰几乎都是标注噪声。
///
/// 能力、偏好这类参数不写进判定——概率表本身已经承载了全部差异，
/// 再叠加 <see cref="GoalkeeperProfile.SidePreference"/> 之类会造成重复计数。
/// </summary>
public static class GoalkeeperTrainer
{
    /// <summary>
    /// 个体格向 15 格真实先验收缩的强度（等效样本数）。
    /// 取 6：15 脚左右的数据里先验占约三成，既压住单格噪声，又留得住真实偏好。
    /// 留一验证表明这是最稳的一档——再小会过拟合到"某格恰好扑出过一次"，
    /// 再大则把 <c>GoalKeeping</c> 明显的门将也拉回平均值。
    /// </summary>
    private const double PriorStrength = 6.0;

    /// <summary>
    /// 点球的总体扑救率基线。用它当超先验，避免"某个门将只录了 10 个点球、
    /// 恰好全是顶级射手罚进"就把整张表压到 0.1——那等于把他判成不会扑救。
    /// </summary>
    private const double BaselineSaveRate = 0.24;

    /// <summary>
    /// 真实点球的 15 格先验（行优先：Low/Mid/High × Left…Right），
    /// 由公开事件数据统计而来——461 脚射正、96 次扑救，覆盖 2015/16 英超、
    /// 2018 &amp; 2022 世界杯、2020 &amp; 2024 欧洲杯、2024 美洲杯、
    /// 2019 &amp; 2023 女足世界杯、部分西甲。
    ///
    /// 每格先用 <c>(扑救 + 2·全局率) / (射正 + 2)</c> 向全局率收缩（全局 0.2082）。
    /// 5 折交叉验证实测：按格建模 logloss <b>0.4775</b>，单一常数 <b>0.5177</b>，
    /// 提升 7～8%，说明"球飞向哪里决定能不能扑到"是真实信号而不是噪声。
    /// 物理上也讲得通：中路（门将已倒地）几乎必进，中高两侧是门将的吃饭区，
    /// 而贴着横梁的高球 62 脚里一次都没扑到。
    ///
    /// 个体样本少时先验占主导，样本变多后自然让位——这正是模型"可进化"的数学含义。
    /// </summary>
    private static readonly double[] ZonePrior =
    {
        0.1745, 0.3006, 0.1726, 0.3720, 0.1552,
        0.2671, 0.4534, 0.0260, 0.4208, 0.2655,
        0.0189, 0.0521, 0.0416, 0.0416, 0.0189,
    };

    /// <summary>超先验的等效样本数：样本越多，越让数据说话。</summary>
    private const double BaselineStrength = 8.0;

    /// <summary>向邻域收缩的强度（等效样本数）。</summary>
    private const double NeighbourStrength = 1.0;

    /// <summary>
    /// 邻域高斯核带宽（单位：格），横向与纵向分开。
    ///
    /// 必须各向异性：横向"左→中→右"是连续变化（门将只是站位与启动方向不同），
    /// 纵向"低→中→高"是**不连续**的物理台阶——低球下地就能碰，贴着横梁的球要跳起来，
    /// 真实数据里高球 62 脚一次都没扑到。用同一个带宽会把中段的 0.4 抹到高段，
    /// 凭空给门将造出一个不存在的"高空能力"。
    /// </summary>
    private const double NeighbourBandwidthHorizontal = 1.0;

    private const double NeighbourBandwidthVertical = 0.35;

    /// <summary>Unity 侧 <c>Lerp(0.82, 1.06, GoalKeeping)</c> 取 1.0 时的中性值。</summary>
    private const float NeutralGoalKeeping = 0.75f;

    private const int Rows = 3;
    private const int Columns = 5;

    /// <summary>
    /// 训练出门将。<paramref name="template"/> 提供身体参数（身高、下地速度等）——
    /// 单机位视频无法可靠测量这些量，硬估出来的数字比继承模板更糟。
    /// </summary>
    public static GoalkeeperTrainingResult Train(
        string name,
        IReadOnlyList<KeeperShotSample> samples,
        GoalkeeperProfile? template = null)
    {
        template ??= new GoalkeeperProfile();
        var warnings = new List<string>();

        var onTarget = samples.Where(sample => sample.OnTarget).ToArray();
        var offTarget = samples.Count - onTarget.Length;
        if (offTarget > 0)
        {
            warnings.Add($"跳过 {offTarget} 条偏出球门的标注（不计入扑救率）。");
        }

        if (onTarget.Length == 0)
        {
            warnings.Add("没有落在门框内的标注，概率表保持模板原值。");
            return new GoalkeeperTrainingResult
            {
                Profile = CloneTemplate(template, name, 0, 0, 0),
                Zones = BuildZones(new double[Rows, Columns], new int[Rows, Columns], new int[Rows, Columns],
                    template.ZoneProbability),
                ShotCount = 0,
                SaveCount = 0,
                OffTargetCount = offTarget,
                GlobalSaveRate = 0,
                Confidence = TrainingConfidence.Insufficient,
                Warnings = warnings,
                SideIndex = 0,
                HeightIndex = 0,
                EstimatedGoalKeeping = 0,
            };
        }

        var estimate = Estimate(onTarget);
        var counts = estimate.Counts;
        var saves = estimate.Saves;
        var probabilities = estimate.Probabilities;
        var saveCount = estimate.SaveCount;
        var emptyZones = counts.Cast<int>().Count(count => count == 0);

        if (emptyZones > 0)
        {
            warnings.Add($"有 {emptyZones} 个区域没有标注数据，已用相邻区域估计填补。");
        }

        var confidence = onTarget.Length switch
        {
            >= 150 => TrainingConfidence.High,
            >= 60 => TrainingConfidence.Medium,
            >= 25 => TrainingConfidence.Low,
            _ => TrainingConfidence.Insufficient,
        };

        if (confidence is TrainingConfidence.Insufficient or TrainingConfidence.Low)
        {
            warnings.Add($"只有 {onTarget.Length} 条有效标注，概率表仍以先验为主，结论仅供参考。");
        }

        var sideIndex = SideIndex(probabilities);
        var heightIndex = HeightIndex(probabilities);
        if (Math.Abs(sideIndex) < 0.15 && Math.Abs(heightIndex) < 0.15)
        {
            warnings.Add("各区域扑救率差异很小，数据量可能还不足以暴露稳定弱点。");
        }

        var quality = CrossValidate(onTarget, folds: 5);
        if (quality.HasData && quality.Improvement <= 0.02)
        {
            warnings.Add("交叉验证显示区域模型还比不上一直猜平均扑救率，建议继续积累标注。");
        }

        var estimatedKeeping = Math.Clamp(((double)saveCount / onTarget.Length - 0.15) / 0.45, 0, 1);

        return new GoalkeeperTrainingResult
        {
            Profile = BuildProfile(template, name, probabilities, onTarget.Length, saveCount,
                (double)saveCount / onTarget.Length),
            Zones = BuildZones(probabilities, counts, saves, _ => 0f),
            ShotCount = onTarget.Length,
            SaveCount = saveCount,
            OffTargetCount = offTarget,
            GlobalSaveRate = (double)saveCount / onTarget.Length,
            Confidence = confidence,
            Warnings = warnings,
            SideIndex = sideIndex,
            HeightIndex = heightIndex,
            EstimatedGoalKeeping = estimatedKeeping,
            Quality = quality,
        };
    }

    /// <summary>
    /// 把训练结果打包成可落盘的模型文件。传入 <paramref name="previous"/> 时会自动续写代数与进化史，
    /// 这样每次追加标注重训都能留下完整轨迹。
    /// </summary>
    public static GoalkeeperModelDocument ToDocument(
        GoalkeeperTrainingResult result,
        string source,
        GoalkeeperModelDocument? previous = null,
        DateTimeOffset? trainedAt = null)
    {
        var zones = result.Zones;
        var trainedOn = (trainedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm:ss");
        var version = (previous?.Version ?? 0) + 1;

        var history = previous?.History is { Count: > 0 } existing
            ? new List<GoalkeeperTrainingHistoryEntry>(existing)
            : [];

        history.Add(new GoalkeeperTrainingHistoryEntry
        {
            Version = version,
            TrainedAt = trainedOn,
            ShotCount = result.ShotCount,
            SaveCount = result.SaveCount,
            GlobalSaveRate = result.GlobalSaveRate,
            Confidence = result.Confidence.Describe(),
            CrossValidationLogLoss = result.Quality.LogLoss,
            Source = source,
        });

        return new GoalkeeperModelDocument
        {
            Version = version,
            TrainedAt = trainedOn,
            Source = source,
            ShotCount = result.ShotCount,
            SaveCount = result.SaveCount,
            OffTargetCount = result.OffTargetCount,
            GlobalSaveRate = result.GlobalSaveRate,
            Confidence = result.Confidence.Describe(),
            SideIndex = result.SideIndex,
            HeightIndex = result.HeightIndex,
            ZoneSamples = zones.Select(zone => zone.Samples).ToArray(),
            ZoneSaves = zones.Select(zone => zone.Saves).ToArray(),
            Warnings = result.Warnings.ToArray(),
            Quality = new TrainingQualityDocument
            {
                Folds = result.Quality.Folds,
                Samples = result.Quality.Samples,
                LogLoss = result.Quality.LogLoss,
                Brier = result.Quality.Brier,
                BaselineLogLoss = result.Quality.BaselineLogLoss,
            },
            History = history,
            Profile = result.Profile,
        };
    }

    /// <summary>门线坐标 → (行, 列)。行 0 = 低，列 0 = 左（射手视角）。</summary>
    public static (int Row, int Column) ZoneIndex(double lateral, double height)
    {
        var column = (int)Math.Floor((lateral + GoalGeometry.HalfWidth) / (GoalGeometry.HalfWidth * 2 / Columns));
        column = Math.Clamp(column, 0, Columns - 1);
        var row = (int)Math.Floor(Math.Max(0, height) / (GoalGeometry.Height / Rows));
        row = Math.Clamp(row, 0, Rows - 1);
        return (row, column);
    }

    private sealed record ZoneEstimate(
        double[,] Probabilities,
        int[,] Counts,
        int[,] Saves,
        int SaveCount,
        double GlobalRate,
        double ObservedRate);

    /// <summary>
    /// 核心估计：逐格 Beta 后验 + 高斯邻域收缩。抽成独立方法是为了让
    /// 交叉验证能复用完全相同的算法，避免"验证用的模型和上线用的模型不是一回事"。
    /// </summary>
    private static ZoneEstimate Estimate(IReadOnlyList<KeeperShotSample> samples)
    {
        var counts = new int[Rows, Columns];
        var saves = new int[Rows, Columns];
        var saveCount = 0;

        foreach (var sample in samples)
        {
            var (row, column) = ZoneIndex(sample.Lateral, sample.Height);
            counts[row, column]++;
            if (!sample.Saved) continue;
            saves[row, column]++;
            saveCount++;
        }

        var globalRate = (saveCount + BaselineStrength * BaselineSaveRate) /
                         (samples.Count + BaselineStrength);
        var local = new double[Rows, Columns];
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Columns; c++)
            {
                local[r, c] = (saves[r, c] + PriorStrength * ZonePrior[r * Columns + c]) /
                              (counts[r, c] + PriorStrength);
            }
        }

        var probabilities = new double[Rows, Columns];
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Columns; c++)
            {
                var neighbourSum = 0.0;
                var neighbourWeight = 0.0;
                for (var dr = 0; dr < Rows; dr++)
                {
                    for (var dc = 0; dc < Columns; dc++)
                    {
                        if (dr == r && dc == c) continue;
                        var dy = (dr - r) / NeighbourBandwidthVertical;
                        var dx = (dc - c) / NeighbourBandwidthHorizontal;
                        var distanceSquared = dy * dy + dx * dx;
                        var weight = Math.Exp(-distanceSquared / 2.0);
                        neighbourSum += weight * local[dr, dc];
                        neighbourWeight += weight;
                    }
                }

                var neighbour = neighbourWeight > 0 ? neighbourSum / neighbourWeight : globalRate;
                var selfWeight = counts[r, c] / (counts[r, c] + NeighbourStrength);
                probabilities[r, c] = Math.Clamp(
                    selfWeight * local[r, c] + (1 - selfWeight) * neighbour, 0.02, 0.98);
            }
        }

        return new ZoneEstimate(probabilities, counts, saves, saveCount, globalRate,
            (double)saveCount / samples.Count);
    }

    /// <summary>
    /// K 折交叉验证：用同一套估计器在留出折上算 log loss / Brier，
    /// 与"每脚都猜全局平均扑救率"的基线比较。这是判断重训是否真的进化的依据。
    /// </summary>
    public static TrainingQuality CrossValidate(
        IReadOnlyList<KeeperShotSample> samples,
        int folds = 5)
    {
        var valid = samples.Where(sample => sample.OnTarget).ToArray();
        if (valid.Length < 10) return TrainingQuality.Empty;

        var k = Math.Clamp(folds, 2, valid.Length);
        var logLoss = 0.0;
        var brier = 0.0;
        var baseline = 0.0;
        var evaluated = 0;

        for (var fold = 0; fold < k; fold++)
        {
            var train = new List<KeeperShotSample>();
            var test = new List<KeeperShotSample>();
            for (var i = 0; i < valid.Length; i++)
            {
                if (i % k == fold) test.Add(valid[i]);
                else train.Add(valid[i]);
            }

            if (train.Count == 0 || test.Count == 0) continue;
            var estimate = Estimate(train);
            var baselineRate = Math.Clamp(estimate.ObservedRate, 0.02, 0.98);

            foreach (var sample in test)
            {
                var (row, column) = ZoneIndex(sample.Lateral, sample.Height);
                var probability = Math.Clamp(estimate.Probabilities[row, column], 0.02, 0.98);
                var observed = sample.Saved ? 1.0 : 0.0;
                logLoss += -(observed * Math.Log(probability) + (1 - observed) * Math.Log(1 - probability));
                brier += (probability - observed) * (probability - observed);
                baseline += -(observed * Math.Log(baselineRate) + (1 - observed) * Math.Log(1 - baselineRate));
                evaluated++;
            }
        }

        return evaluated == 0
            ? TrainingQuality.Empty
            : new TrainingQuality(k, evaluated, logLoss / evaluated, brier / evaluated, baseline / evaluated);
    }

    private static GoalkeeperProfile BuildProfile(
        GoalkeeperProfile template,
        string name,
        double[,] probabilities,
        int shotCount,
        int saveCount,
        double saveRate)
    {
        var profile = CloneTemplate(template, name, shotCount, saveCount, saveRate);
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Columns; c++)
            {
                profile.ProbabilityMap[ZoneKey(r, c)] = (float)Math.Round(probabilities[r, c], 4);
            }
        }

        return profile;
    }

    private static GoalkeeperProfile CloneTemplate(
        GoalkeeperProfile template,
        string name,
        int shotCount,
        int saveCount,
        double saveRate)
    {
        var map = new Dictionary<string, float>(StringComparer.Ordinal);
        foreach (var key in GoalkeeperProfile.ZoneKeys)
        {
            map[key] = template.ProbabilityMap.TryGetValue(key, out var value)
                ? value
                : DefaultZoneValue(key);
        }

        return new GoalkeeperProfile
        {
            Name = name,
            DisplayName = name,
            Description = shotCount > 0
                ? $"训练自 {shotCount} 脚射门（扑出 {saveCount}，{saveRate:P0}）"
                : "尚无训练数据",
            Height = template.Height,
            DiveSpeed = template.DiveSpeed,
            JumpDistance = template.JumpDistance,
            JumpHeight = template.JumpHeight,
            Reach = template.Reach,
            TendGoalSpeed = template.TendGoalSpeed,
            TendGoalDistance = template.TendGoalDistance,
            // 概率表已经包含能力差异，这里保持中性，避免 Unity 侧二次缩放
            GoalKeeping = NeutralGoalKeeping,
            SidePreference = 0f,
            HeightPreference = 0f,
            ProbabilityMap = map,
        };
    }

    private static float DefaultZoneValue(string key) => 0.75f;

    private static IReadOnlyList<ZoneTrainingStat> BuildZones(
        double[,] probabilities,
        int[,] counts,
        int[,] saves,
        Func<string, float> fallback)
    {
        var zones = new List<ZoneTrainingStat>(Rows * Columns);
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Columns; c++)
            {
                var key = ZoneKey(r, c);
                var probability = probabilities[r, c] > 0 ? probabilities[r, c] : fallback(key);
                zones.Add(new ZoneTrainingStat(r, c, key, counts[r, c], saves[r, c], probability));
            }
        }

        return zones;
    }

    private static string ZoneKey(int row, int column) =>
        GoalkeeperProfile.ZoneKeys[row * Columns + column];

    /// <summary>左右倾向：右三列与左三列的平均扑救率差，按 Unity 偏好项的量纲缩放。</summary>
    private static double SideIndex(double[,] probabilities)
    {
        var left = 0.0;
        var right = 0.0;
        for (var r = 0; r < Rows; r++)
        {
            left += probabilities[r, 0] + probabilities[r, 1];
            right += probabilities[r, 3] + probabilities[r, 4];
        }

        return Math.Clamp((right - left) / 6 / 0.05, -1, 1);
    }

    /// <summary>高低倾向：高三行与低三行的平均扑救率差。</summary>
    private static double HeightIndex(double[,] probabilities)
    {
        var low = 0.0;
        var high = 0.0;
        for (var c = 0; c < Columns; c++)
        {
            low += probabilities[0, c];
            high += probabilities[2, c];
        }

        return Math.Clamp((high - low) / Columns / 0.05, -1, 1);
    }
}
