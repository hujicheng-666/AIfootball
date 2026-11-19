using Assets.SuperGoalie.Scripts.Entities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Assets.SuperGoalie.Scripts.Trajectories
{
    public struct TrajectorySample
    {
        public float Time;
        public Vector3 CenterPosition;

        public TrajectorySample(float time, Vector3 centerPosition)
        {
            Time = time;
            CenterPosition = centerPosition;
        }
    }

    /// <summary>
    /// A time-based trajectory whose positions are ball-centre positions in Unity world space.
    /// </summary>
    public sealed class BallTrajectory
    {
        readonly List<TrajectorySample> _samples;

        public BallTrajectory(List<TrajectorySample> samples)
        {
            if (samples == null || samples.Count < 2)
                throw new ArgumentException("轨迹至少需要两个采样点。", "samples");

            _samples = samples;
        }

        public int SampleCount { get { return _samples.Count; } }

        public IList<TrajectorySample> Samples { get { return _samples.AsReadOnly(); } }

        public float Duration { get { return _samples[_samples.Count - 1].Time; } }

        public Vector3 InitialCenter { get { return _samples[0].CenterPosition; } }

        public Vector3 FinalCenter { get { return _samples[_samples.Count - 1].CenterPosition; } }

        public float MaxCenterY
        {
            get
            {
                float maxY = _samples[0].CenterPosition.y;
                for (int i = 1; i < _samples.Count; ++i)
                    maxY = Mathf.Max(maxY, _samples[i].CenterPosition.y);
                return maxY;
            }
        }

        public Vector3 FindCenterClosestToPlane(Vector3 planePoint, Vector3 planeNormal)
        {
            planeNormal = planeNormal.sqrMagnitude > Mathf.Epsilon ? planeNormal.normalized : Vector3.forward;
            Vector3 closest = InitialCenter;
            float closestAbsDistance = Mathf.Abs(Vector3.Dot(closest - planePoint, planeNormal));

            for (int i = 1; i < _samples.Count; ++i)
            {
                Vector3 previous = _samples[i - 1].CenterPosition;
                Vector3 current = _samples[i].CenterPosition;
                float previousDistance = Vector3.Dot(previous - planePoint, planeNormal);
                float currentDistance = Vector3.Dot(current - planePoint, planeNormal);
                if ((previousDistance <= 0f && currentDistance >= 0f) || (previousDistance >= 0f && currentDistance <= 0f))
                {
                    float t = Mathf.InverseLerp(previousDistance, currentDistance, 0f);
                    return Vector3.LerpUnclamped(previous, current, t);
                }

                float absDistance = Mathf.Abs(currentDistance);
                if (absDistance < closestAbsDistance)
                {
                    closest = current;
                    closestAbsDistance = absDistance;
                }
            }

            return closest;
        }

        public float AverageSpeed
        {
            get
            {
                float distance = 0f;
                for (int i = 1; i < _samples.Count; ++i)
                    distance += Vector3.Distance(_samples[i - 1].CenterPosition, _samples[i].CenterPosition);

                return Duration > Mathf.Epsilon ? distance / Duration : 0f;
            }
        }

        public Vector3 EvaluateCenter(float time)
        {
            if (time <= 0f)
                return InitialCenter;
            if (time >= Duration)
                return FinalCenter;

            int upperIndex = FindUpperSampleIndex(time);
            TrajectorySample a = _samples[upperIndex - 1];
            TrajectorySample b = _samples[upperIndex];
            float t = Mathf.InverseLerp(a.Time, b.Time, time);
            return Vector3.LerpUnclamped(a.CenterPosition, b.CenterPosition, t);
        }

        public Vector3 EvaluateVelocity(float time)
        {
            int upperIndex;
            if (time <= 0f)
                upperIndex = 1;
            else if (time >= Duration)
                upperIndex = _samples.Count - 1;
            else
                upperIndex = FindUpperSampleIndex(time);

            TrajectorySample a = _samples[upperIndex - 1];
            TrajectorySample b = _samples[upperIndex];
            float deltaTime = b.Time - a.Time;
            return deltaTime > Mathf.Epsilon
                ? (b.CenterPosition - a.CenterPosition) / deltaTime
                : Vector3.zero;
        }

        public Vector3 FindClosestCenter(Vector3 worldPoint)
        {
            Vector3 closest = InitialCenter;
            float closestSqrDistance = (closest - worldPoint).sqrMagnitude;

            for (int i = 1; i < _samples.Count; ++i)
            {
                Vector3 candidate = _samples[i].CenterPosition;
                float sqrDistance = (candidate - worldPoint).sqrMagnitude;
                if (sqrDistance < closestSqrDistance)
                {
                    closest = candidate;
                    closestSqrDistance = sqrDistance;
                }
            }

            return closest;
        }

        int FindUpperSampleIndex(float time)
        {
            int low = 1;
            int high = _samples.Count - 1;

            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (_samples[middle].Time < time)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }
    }

    public static class CsvTrajectoryLoader
    {
        public static BallTrajectory Load(string path, Goal goal)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("CSV 文件路径不能为空。", "path");
            if (!File.Exists(path))
                throw new FileNotFoundException("找不到 CSV 文件。", path);
            if (goal == null)
                throw new ArgumentNullException("goal");

            string csvText = File.ReadAllText(path);
            return Parse(csvText, goal.CsvBallCenterToWorld);
        }

        public static BallTrajectory Parse(string csvText, Goal goal)
        {
            return Parse(csvText, goal.CsvBallCenterToWorld);
        }

        /// <summary>从 CSV 文本解析，用自定义坐标转换函数映射到世界空间</summary>
        public static BallTrajectory Parse(string csvText, System.Func<Vector3, Vector3> toWorld)
        {
            if (string.IsNullOrWhiteSpace(csvText))
                throw new FormatException("CSV 文件为空。要求列顺序为 time,x,y,z。");

            string[] lines = csvText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            List<RawSample> rawSamples = new List<RawSample>();
            bool firstDataOrHeaderSeen = false;

            for (int lineIndex = 0; lineIndex < lines.Length; ++lineIndex)
            {
                string line = lines[lineIndex].Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                string[] columns = line.Split(',');
                if (columns.Length != 4)
                    throw new FormatException(string.Format("CSV 第 {0} 行必须正好包含 4 列：time,x,y,z。", lineIndex + 1));

                float time;
                Vector3 csvPosition;
                if (!TryParseRow(columns, out time, out csvPosition))
                {
                    if (!firstDataOrHeaderSeen && IsHeader(columns))
                    {
                        firstDataOrHeaderSeen = true;
                        continue;
                    }

                    throw new FormatException(string.Format("CSV 第 {0} 行包含无效数字。请使用英文小数点。", lineIndex + 1));
                }

                firstDataOrHeaderSeen = true;
                if (!IsFinite(time) || !IsFinite(csvPosition.x) || !IsFinite(csvPosition.y) || !IsFinite(csvPosition.z))
                    throw new FormatException(string.Format("CSV 第 {0} 行包含 NaN 或无穷大。", lineIndex + 1));

                if (rawSamples.Count > 0 && time <= rawSamples[rawSamples.Count - 1].Time)
                    throw new FormatException(string.Format("CSV 第 {0} 行的时间必须严格大于上一行。", lineIndex + 1));

                rawSamples.Add(new RawSample(time, csvPosition));
            }

            if (rawSamples.Count < 2)
                throw new FormatException("CSV 至少需要两个有效采样点。第一行可以是 time,x,y,z 表头。");

            float firstTime = rawSamples[0].Time;
            List<TrajectorySample> worldSamples = new List<TrajectorySample>(rawSamples.Count);
            for (int i = 0; i < rawSamples.Count; ++i)
            {
                RawSample raw = rawSamples[i];
                float normalizedTime = raw.Time - firstTime;
                Vector3 worldCenter = toWorld(raw.CsvPosition);
                worldSamples.Add(new TrajectorySample(normalizedTime, worldCenter));
            }

            if (worldSamples[worldSamples.Count - 1].Time <= Mathf.Epsilon)
                throw new FormatException("CSV 轨迹总时长必须大于 0 秒。");

            return new BallTrajectory(worldSamples);
        }

        static bool TryParseRow(string[] columns, out float time, out Vector3 position)
        {
            const NumberStyles styles = NumberStyles.Float;
            CultureInfo culture = CultureInfo.InvariantCulture;
            float x = 0f;
            float y = 0f;
            float z = 0f;

            bool valid = float.TryParse(columns[0].Trim().TrimStart('\ufeff'), styles, culture, out time)
                && float.TryParse(columns[1].Trim(), styles, culture, out x)
                && float.TryParse(columns[2].Trim(), styles, culture, out y)
                && float.TryParse(columns[3].Trim(), styles, culture, out z);

            position = valid ? new Vector3(x, y, z) : Vector3.zero;
            return valid;
        }

        static bool IsHeader(string[] columns)
        {
            return string.Equals(columns[0].Trim().TrimStart('\ufeff'), "time", StringComparison.OrdinalIgnoreCase)
                && string.Equals(columns[1].Trim(), "x", StringComparison.OrdinalIgnoreCase)
                && string.Equals(columns[2].Trim(), "y", StringComparison.OrdinalIgnoreCase)
                && string.Equals(columns[3].Trim(), "z", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        struct RawSample
        {
            public float Time;
            public Vector3 CsvPosition;

            public RawSample(float time, Vector3 csvPosition)
            {
                Time = time;
                CsvPosition = csvPosition;
            }
        }
    }
}
