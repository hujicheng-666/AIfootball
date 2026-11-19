using System.Text.Json;
using AIfootball.Core.Models;

namespace AIfootball.Core.Services;

/// <summary>从 StatsBomb 事件数据里解析出的一条点球记录。</summary>
public sealed record StatsBombPenalty(
    int MatchId,
    int Period,
    string Shooter,
    string Keeper,
    string Outcome,
    double Lateral,
    double Height,
    bool Saved,
    bool OnTarget)
{
    public bool IsShootout => Period == 5;
}

/// <summary>一次导入的统计，便于界面告诉用户"读了多少、跳过了多少、为什么"。</summary>
public sealed record StatsBombImportSummary(
    int FilesRead,
    int PenaltyEvents,
    int UsableSamples,
    int OffTarget,
    int MissingLocation,
    IReadOnlyList<string> KeeperNames,
    IReadOnlyList<KeeperShotSample> Samples);

/// <summary>单个事件文件的解析结果。</summary>
public sealed record StatsBombPenaltyParse(
    IReadOnlyList<StatsBombPenalty> Penalties,
    int MissingLocation);

/// <summary>
/// StatsBomb 公开事件数据（open-data）里的点球导入器。
///
/// 这类数据的好处是"落点 + 是否扑出"本来就标注好了，不用自己看录像。
/// 坐标换算：StatsBomb 用码制 120×80 的球场，球门中心 y=40、门柱 y=36/44、
/// 横梁 z=2.67 码；1 码 = 0.9144 米，正好得到 7.32 × 2.44 的标准球门。
/// <c>shot.end_location</c> 是球穿过门线处的位置，所以能直接当作落点用。
/// </summary>
public static class StatsBombPenaltyImporter
{
    private const double YardsToMeters = 0.9144;
    private const double GoalCenterYards = 40.0;
    private const double HalfGoalYards = 4.0;
    private const double CrossbarYards = 2.67;

    /// <summary>
    /// 解析单个比赛事件文件。只保留点球（含点球大战），并按门将归类。
    /// <paramref name="matchId"/> 为 0 时尝试从 JSON 里读，读不到就记为 0。
    /// </summary>
    public static StatsBombPenaltyParse ParseEvents(string json, int matchId = 0)
    {
        var penalties = new List<StatsBombPenalty>();
        var missingLocation = 0;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException)
        {
            return new StatsBombPenaltyParse(penalties, missingLocation);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new StatsBombPenaltyParse(penalties, missingLocation);
            }

            // Shot 事件本身不总带 goalkeeper 字段，门将只能从首发阵容反推：
            // 射手所在队的对手，其 lineup 里 position=Goalkeeper 的那个人。
            var keepers = ReadGoalkeepers(document.RootElement);

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;
                if (GetName(element, "type") != "Shot") continue;
                if (!element.TryGetProperty("shot", out var shot) || shot.ValueKind != JsonValueKind.Object) continue;
                if (GetName(shot, "type") != "Penalty") continue;

                var outcome = GetName(shot, "outcome");
                var location = ReadLocation(shot, "end_location");
                if (location is null)
                {
                    missingLocation++;
                    continue;
                }

                var lateralYards = location.Value.Y - GoalCenterYards;
                var heightYards = location.Value.Z;
                var insideGoal = Math.Abs(lateralYards) <= HalfGoalYards
                                 && heightYards >= 0
                                 && heightYards <= CrossbarYards;

                var keeper = GetName(shot, "goalkeeper");
                if (string.IsNullOrWhiteSpace(keeper))
                {
                    var shootingTeam = GetName(element, "team");
                    keeper = keepers
                        .Where(pair => !string.Equals(pair.Key, shootingTeam, StringComparison.OrdinalIgnoreCase))
                        .Select(pair => pair.Value)
                        .FirstOrDefault() ?? "";
                }

                penalties.Add(new StatsBombPenalty(
                    MatchId: matchId,
                    Period: GetInt(element, "period", 0),
                    Shooter: GetName(element, "player"),
                    Keeper: keeper,
                    Outcome: outcome,
                    Lateral: lateralYards * YardsToMeters,
                    Height: heightYards * YardsToMeters,
                    Saved: outcome is "Saved" or "Saved to Post",
                    OnTarget: insideGoal));
            }
        }

        return new StatsBombPenaltyParse(penalties, missingLocation);
    }

    /// <summary>从首发阵容事件里读出每支球队的门将名。</summary>
    private static Dictionary<string, string> ReadGoalkeepers(JsonElement events)
    {
        var keepers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in events.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            if (GetName(element, "type") != "Starting XI") continue;
            if (!element.TryGetProperty("team", out var team) || team.ValueKind != JsonValueKind.Object) continue;
            var teamName = GetString(team, "name");
            if (string.IsNullOrWhiteSpace(teamName)) continue;

            if (!element.TryGetProperty("tactics", out var tactics)
                || !tactics.TryGetProperty("lineup", out var lineup)
                || lineup.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in lineup.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("position", out var position)
                    || position.ValueKind != JsonValueKind.Object
                    || GetString(position, "name") != "Goalkeeper")
                {
                    continue;
                }

                var name = GetName(entry, "player");
                if (!string.IsNullOrWhiteSpace(name)) keepers[teamName] = name;
                break;
            }
        }

        return keepers;
    }

    /// <summary>
    /// 把解析结果转成训练样本。只保留射正的球（偏出的球不影响区域扑救率），
    /// 并且可以按门将名字过滤，方便"只训练某一门将"。
    /// </summary>
    public static StatsBombImportSummary Import(
        IEnumerable<(int MatchId, string Json)> eventsFiles,
        string? keeperFilter = null)
    {
        var samples = new List<KeeperShotSample>();
        var keepers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedClips = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = 0;
        var events = 0;
        var offTarget = 0;
        var missing = 0;

        foreach (var (matchId, json) in eventsFiles)
        {
            files++;
            var parsed = ParseEvents(json, matchId);
            events += parsed.Penalties.Count;
            missing += parsed.MissingLocation;

            foreach (var penalty in parsed.Penalties)
            {
                if (!string.IsNullOrWhiteSpace(penalty.Keeper)) keepers.Add(penalty.Keeper);
                if (!penalty.OnTarget)
                {
                    offTarget++;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(keeperFilter)
                    && !string.Equals(penalty.Keeper, keeperFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var clip = $"sb{matchId}_p{penalty.Period}_{Slug(penalty.Shooter)}";
                if (!usedClips.Add(clip))
                {
                    // 同一场同一节同一射手罚两次（极少见），加序号避免标注互相覆盖
                    var index = 2;
                    string candidate;
                    do
                    {
                        candidate = $"{clip}_{index++}";
                    } while (!usedClips.Add(candidate));

                    clip = candidate;
                }

                samples.Add(new KeeperShotSample(clip, penalty.Lateral, penalty.Height, penalty.Saved));
            }
        }

        return new StatsBombImportSummary(
            files, events, samples.Count, offTarget, missing,
            keepers.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray(),
            samples);
    }

    /// <summary>直接写出标注文件，让导入的数据和手工标注走同一条训练链路。</summary>
    public static int WriteAnnotations(
        GoalkeeperTrainingStore store,
        string keeper,
        IEnumerable<KeeperShotSample> samples,
        string source)
    {
        var written = 0;
        foreach (var sample in samples)
        {
            store.SaveAnnotation(keeper, new KeeperClipAnnotation
            {
                Clip = sample.Clip,
                Lateral = sample.Lateral,
                Height = sample.Height,
                Saved = sample.Saved,
                Note = source,
            });
            written++;
        }

        return written;
    }

    private static (double X, double Y, double Z)? ReadLocation(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return null;
        var values = new List<double>(3);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number) return null;
            values.Add(item.GetDouble());
        }

        return values.Count switch
        {
            >= 3 => (values[0], values[1], values[2]),
            2 => (values[0], values[1], 0),
            _ => null,
        };
    }

    private static string GetName(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object) return "";
        return GetString(value, "name");
    }

    private static string GetString(JsonElement parent, string property) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static int GetInt(JsonElement parent, string property, int fallback) =>
        parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : fallback;

    private static string Slug(string value)
    {
        var cleaned = new string(value
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray());
        return cleaned.Length <= 24 ? cleaned : cleaned[..24];
    }
}
