using AIfootball.Core.Models;
using AIfootball.Core.Services;

namespace AIfootball.Core.Tests;

public sealed class GoalCrossEventStoreTests
{
    private static string WriteTemp(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gce_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Load_RealShape_ParsesFields()
    {
        const string json = """
        {
          "video": "D:/v.mp4",
          "total_hits": 506,
          "events": [
            {"seg":0,"t_start":2.0,"t_end":2.24,"t_cross":2.12,"n_frames":7,
             "max_conf":0.69,"crossed":true,"inside_any":true,"seg_t0":0.0,"seg_t1":4.76},
            {"seg":3,"t_start":11.56,"t_end":11.76,"t_cross":null,"n_frames":6,
             "max_conf":0.45,"crossed":false,"inside_any":false,"seg_t0":9.36,"seg_t1":13.48}
          ]
        }
        """;
        var path = WriteTemp(json);
        try
        {
            var doc = GoalCrossEventStore.Load(path);
            Assert.NotNull(doc);
            Assert.Equal("D:/v.mp4", doc!.Video);
            Assert.Equal(506, doc.TotalHits);
            Assert.Equal(2, doc.Events.Count);
            Assert.Equal(1, doc.CrossedCount);

            var e = doc.Events[0];
            Assert.Equal(0, e.Seg);
            Assert.Equal(2.0, e.TStart);
            Assert.Equal(2.24, e.TEnd);
            Assert.Equal(2.12, e.TCross);
            Assert.Equal(7, e.NFrames);
            Assert.True(e.Crossed);
            Assert.True(e.InsideAny);
            Assert.Equal(4.76, e.SegT1);

            var second = doc.Events[1];
            Assert.Null(second.TCross);
            Assert.False(second.Crossed);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_SortsByStartTime()
    {
        const string json = """
        {"events":[
          {"seg":2,"t_start":9.0,"t_end":9.5,"crossed":false},
          {"seg":1,"t_start":3.0,"t_end":3.5,"crossed":true}
        ]}
        """;
        var path = WriteTemp(json);
        try
        {
            var doc = GoalCrossEventStore.Load(path);
            Assert.NotNull(doc);
            Assert.Equal(3.0, doc!.Events[0].TStart);
            Assert.Equal(9.0, doc.Events[1].TStart);
            Assert.Null(doc.Video);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nope_{Guid.NewGuid():N}.json");
        Assert.Null(GoalCrossEventStore.Load(path));
    }

    [Fact]
    public void Load_MalformedJson_ReturnsNull()
    {
        var path = WriteTemp("{ this is not valid json ");
        try { Assert.Null(GoalCrossEventStore.Load(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Display_CrossedEvent_ShowsCrossTime()
    {
        var e = new GoalCrossEvent
        {
            Seg = 0, TStart = 2.0, TEnd = 2.24, TCross = 2.12, Crossed = true,
        };
        Assert.Contains("过线", e.Display);
        Assert.Contains("2.12", e.Display);
        Assert.Equal(e.Display, e.ToString());
    }

    [Fact]
    public void Display_InsideButNotCrossed_ShowsInsideFlag()
    {
        var e = new GoalCrossEvent { Seg = 3, TStart = 11.56, TEnd = 11.76, InsideAny = true };
        Assert.Contains("框内", e.Display);
        Assert.DoesNotContain("过线2", e.Display);
    }
}
