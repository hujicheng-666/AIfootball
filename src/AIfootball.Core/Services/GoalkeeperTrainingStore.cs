using System.Text.Encodings.Web;
using System.Text.Json;
using AIfootball.Core.Models;

namespace AIfootball.Core.Services;

/// <summary>
/// 门将训练数据的磁盘布局与读写。标注文件是唯一事实来源，模型永远可以从标注重算出来，
/// 所以"可进化"的实现就是：追加标注 → 重跑训练 → 模型代数 +1，历史全部保留。
///
/// 目录结构：
/// <code>
/// data/goalkeeper_training/&lt;keeper&gt;/calibration.json     ← 门框四点（同一机位共享）
/// data/goalkeeper_training/&lt;keeper&gt;/clips/&lt;clip&gt;.json    ← 每个片段一条标注
/// data/goalkeepers/models/&lt;keeper&gt;.model.json           ← 训练产物（含进化史）
/// data/goalkeepers/&lt;keeper&gt;.json                        ← 发布后的 Unity 契约文件
/// </code>
/// </summary>
public sealed class GoalkeeperTrainingStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public GoalkeeperTrainingStore(string workspaceRoot)
    {
        WorkspaceRoot = workspaceRoot;
        TrainingRoot = Path.Combine(workspaceRoot, "data", "goalkeeper_training");
        ModelsRoot = Path.Combine(workspaceRoot, "data", "goalkeepers", "models");
        GoalkeepersRoot = Path.Combine(workspaceRoot, "data", "goalkeepers");
    }

    public string WorkspaceRoot { get; }
    public string TrainingRoot { get; }
    public string ModelsRoot { get; }
    public string GoalkeepersRoot { get; }

    public string KeeperDirectory(string keeper) => Path.Combine(TrainingRoot, Safe(keeper));
    public string ClipsDirectory(string keeper) => Path.Combine(KeeperDirectory(keeper), "clips");
    public string CalibrationsDirectory(string keeper) => Path.Combine(KeeperDirectory(keeper), "calibrations");
    public string ModelPath(string keeper) => Path.Combine(ModelsRoot, Safe(keeper) + ".model.json");

    /// <summary>列出所有已有标注目录的门将。</summary>
    public IReadOnlyList<string> ListKeepers()
    {
        if (!Directory.Exists(TrainingRoot)) return [];
        return Directory.GetDirectories(TrainingRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    public GoalFrameCalibration? LoadCalibration(string keeper)
    {
        var path = Path.Combine(KeeperDirectory(keeper), "calibration.json");
        return File.Exists(path) ? ReadJson<GoalFrameCalibration>(path) : null;
    }

    public void SaveCalibration(string keeper, GoalFrameCalibration calibration)
    {
        Directory.CreateDirectory(KeeperDirectory(keeper));
        WriteJson(Path.Combine(KeeperDirectory(keeper), "calibration.json"), calibration);
    }

    /// <summary>
    /// 某个视频里每一脚的门框标定。合集是多机位混剪，一脚一套，按这一脚的起点归档。
    /// </summary>
    public VideoCalibrationDocument? LoadVideoCalibrations(string keeper, string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath)) return null;
        var path = VideoCalibrationPath(keeper, videoPath);
        if (!File.Exists(path)) return null;
        var document = ReadJson<VideoCalibrationDocument>(path);
        if (document is null) return null;
        document.Shots.Sort((a, b) => a.ShotStart.CompareTo(b.ShotStart));
        return document.Shots.Count > 0 ? document : null;
    }

    /// <summary>给某一脚写入标定（同一脚重标覆盖）。</summary>
    public void SaveVideoCalibration(string keeper, string videoPath, double shotStart,
                                     GoalFrameCalibration calibration)
    {
        var path = VideoCalibrationPath(keeper, videoPath);
        var document = (File.Exists(path) ? ReadJson<VideoCalibrationDocument>(path) : null)
                       ?? new VideoCalibrationDocument
                       {
                           Video = Path.GetFileNameWithoutExtension(videoPath),
                       };
        document.Set(shotStart, calibration);
        WriteJson(path, document);
    }

    /// <summary>某一脚的门框；没标过返回 null（不沿用别的脚）。</summary>
    public GoalFrameCalibration? ResolveCalibration(string keeper, string videoPath, double shotStart) =>
        LoadVideoCalibrations(keeper, videoPath)?.ForShot(shotStart);

    /// <summary>
    /// 删掉某一脚的门框标定。返回是否真的删到了。
    /// 全片删空时把文件也一起删掉，免得留下一堆空壳 json 让"已标 N 脚"算错。
    /// </summary>
    public bool DeleteVideoCalibration(string keeper, string videoPath, double shotStart)
    {
        if (string.IsNullOrWhiteSpace(videoPath)) return false;
        var path = VideoCalibrationPath(keeper, videoPath);
        if (!File.Exists(path)) return false;

        var document = ReadJson<VideoCalibrationDocument>(path);
        if (document is null || !document.Remove(shotStart)) return false;

        if (document.Shots.Count == 0) File.Delete(path);
        else WriteJson(path, document);
        return true;
    }

    private string VideoCalibrationPath(string keeper, string videoPath) =>
        Path.Combine(CalibrationsDirectory(keeper),
            Safe(Path.GetFileNameWithoutExtension(videoPath)) + ".json");

    /// <summary>列出已标注的片段名（按名称排序，保证训练顺序稳定）。</summary>
    public IReadOnlyList<string> ListAnnotatedClips(string keeper)
    {
        var directory = ClipsDirectory(keeper);
        if (!Directory.Exists(directory)) return [];
        return Directory.GetFiles(directory, "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public KeeperClipAnnotation? LoadAnnotation(string keeper, string clip)
    {
        var path = Path.Combine(ClipsDirectory(keeper), Safe(clip) + ".json");
        return File.Exists(path) ? ReadJson<KeeperClipAnnotation>(path) : null;
    }

    public IReadOnlyList<KeeperClipAnnotation> LoadAnnotations(string keeper)
    {
        var annotations = new List<KeeperClipAnnotation>();
        foreach (var clip in ListAnnotatedClips(keeper))
        {
            var annotation = LoadAnnotation(keeper, clip);
            if (annotation is null) continue;
            annotations.Add(annotation);
        }

        return annotations;
    }

    public void SaveAnnotation(string keeper, KeeperClipAnnotation annotation)
    {
        Directory.CreateDirectory(ClipsDirectory(keeper));
        // 合集视频用"视频名 @ 秒数"区分同一文件里的多脚点球，不能按扩展名截断；
        // 单片段仍按旧约定去掉扩展名，保持已有标注文件名不变。
        annotation.Clip = annotation.Clip.Contains(" @ ", StringComparison.Ordinal)
            ? annotation.Clip.Trim()
            : Path.GetFileNameWithoutExtension(annotation.Clip);
        annotation.MarkedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
        WriteJson(Path.Combine(ClipsDirectory(keeper), Safe(annotation.Clip) + ".json"), annotation);
    }

    public bool DeleteAnnotation(string keeper, string clip)
    {
        var path = Path.Combine(ClipsDirectory(keeper), Safe(clip) + ".json");
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>读出全部标注作为训练样本。</summary>
    public IReadOnlyList<KeeperShotSample> LoadSamples(string keeper) =>
        LoadAnnotations(keeper).Select(annotation => annotation.ToSample()).ToArray();

    public GoalkeeperModelDocument? LoadModel(string keeper)
    {
        var path = ModelPath(keeper);
        return File.Exists(path) ? ReadJson<GoalkeeperModelDocument>(path) : null;
    }

    /// <summary>训练并落盘模型文件（自动续写代数与进化史）。返回新模型。</summary>
    public GoalkeeperModelDocument TrainAndSave(
        string keeper,
        GoalkeeperProfile? template = null,
        string? source = null)
    {
        var samples = LoadSamples(keeper);
        var result = GoalkeeperTrainer.Train(keeper, samples, template);
        var previous = LoadModel(keeper);
        var document = GoalkeeperTrainer.ToDocument(
            result,
            source ?? $"{TrainingRoot}（{samples.Count} 条标注）",
            previous);

        Directory.CreateDirectory(ModelsRoot);
        WriteJson(ModelPath(keeper), document);
        return document;
    }

    /// <summary>
    /// 把模型发布成 Unity 直接读取的契约文件。只写判定需要的字段，
    /// 训练血统留在 models/ 里，避免污染门将库。
    /// </summary>
    public string Publish(GoalkeeperProfile profile)
    {
        Directory.CreateDirectory(GoalkeepersRoot);
        var path = Path.Combine(GoalkeepersRoot, Safe(profile.Name) + ".json");
        WriteJson(path, profile);
        return path;
    }

    /// <summary>把标注目录重命名为新的门将名（用于"另存为"）。</summary>
    public void RenameKeeper(string from, string to)
    {
        var source = KeeperDirectory(from);
        var destination = KeeperDirectory(to);
        if (!Directory.Exists(source) || Directory.Exists(destination)) return;
        Directory.Move(source, destination);
    }

    private static T? ReadJson<T>(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ReadOptions);
        }
        catch (JsonException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(value, WriteOptions));
    }

    /// <summary>去掉文件名非法字符，防止标注名把文件写到目录外面。</summary>
    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(character => !invalid.Contains(character)).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "unnamed" : cleaned;
    }
}
