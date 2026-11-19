using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Assets.SuperGoalie.Scripts.Trajectories;
using PenaltyKickPlatform.Coordinate;
using PenaltyKickPlatform.History;
using UnityEngine;

namespace PenaltyKickPlatform.Analysis
{
    /// <summary>
    /// Creates an explainable shooter profile from the trajectory CSV files kept in training history.
    /// All measures are computed in the penalty-coordinate system, so they remain valid when the scene moves.
    /// </summary>
    public sealed class ShooterProfile
    {
        public int SampleCount;
        public int ValidTrajectoryCount;
        public int DuplicateTrajectoryCount;
        public float AverageSpeed;
        public float AverageTargetOffset;
        public float AverageTargetHeight;
        public float TargetSpread;
        public int LeftCount;
        public int CenterCount;
        public int RightCount;
        public string PreferredSide;
        public string PreferredHeight;
        public string PowerTrait;
        public string ConsistencyTrait;

        public string Summary
        {
            get
            {
                string result = "有效训练样本 " + ValidTrajectoryCount + " 条";
                if (DuplicateTrajectoryCount > 0)
                    result += "（已忽略重复 " + DuplicateTrajectoryCount + " 条）";
                result += "\n"
                    + "主打 " + PreferredSide + " · " + PreferredHeight + "\n"
                    + PowerTrait + " · " + ConsistencyTrait + "\n"
                    + "平均球速 " + AverageSpeed.ToString("0.0") + " m/s   落点横向 "
                    + AverageTargetOffset.ToString("0.00") + " m   高度 " + AverageTargetHeight.ToString("0.00") + " m";
                return result + "\n仅使用 CSV 轨迹，不读取门将或模拟结果";
            }
        }
    }

    public static class ShooterProfileAnalyzer
    {
        private const float CenterLaneHalfWidth = 0.55f;

        public static ShooterProfile Build(CsvHistoryStore history, PenaltyCoordinateSystem coordinates)
        {
            if (history == null)
                throw new ArgumentNullException("history");
            if (coordinates == null)
                throw new ArgumentNullException("coordinates");

            ShooterProfile profile = new ShooterProfile();
            float sumOffset = 0f;
            float sumHeight = 0f;
            float sumSpeed = 0f;
            float sumOffsetSquared = 0f;
            HashSet<string> uniqueTrajectories = new HashSet<string>();

            for (int index = 0; index < history.Entries.Count; index++)
            {
                CsvHistoryEntry entry = history.Entries[index];
                if (entry == null)
                    continue;

                try
                {
                    BallTrajectory trajectory = CsvTrajectoryLoader.Parse(history.Read(entry.Id), coordinates.ToWorld);
                    if (!uniqueTrajectories.Add(CreateTrajectorySignature(trajectory)))
                    {
                        profile.DuplicateTrajectoryCount++;
                        continue;
                    }
                    Vector3 target = trajectory.FindCenterClosestToPlane(coordinates.Origin, coordinates.XAxis);
                    Vector3 localTarget = target - coordinates.Origin;
                    float offset = Vector3.Dot(localTarget, coordinates.YAxis);
                    float height = Vector3.Dot(localTarget, Vector3.up);

                    profile.ValidTrajectoryCount++;
                    sumOffset += offset;
                    sumOffsetSquared += offset * offset;
                    sumHeight += height;
                    sumSpeed += trajectory.AverageSpeed;

                    if (offset > CenterLaneHalfWidth)
                        profile.LeftCount++;
                    else if (offset < -CenterLaneHalfWidth)
                        profile.RightCount++;
                    else
                        profile.CenterCount++;

                }
                catch (Exception exception)
                {
                    Debug.LogWarning("Skipped invalid training trajectory '" + entry.DisplayName + "': " + exception.Message);
                }
            }

            if (profile.ValidTrajectoryCount == 0)
                return profile;

            float count = profile.ValidTrajectoryCount;
            profile.SampleCount = profile.ValidTrajectoryCount;
            profile.AverageTargetOffset = sumOffset / count;
            profile.AverageTargetHeight = sumHeight / count;
            profile.AverageSpeed = sumSpeed / count;
            profile.TargetSpread = Mathf.Sqrt(Mathf.Max(0f, sumOffsetSquared / count
                - profile.AverageTargetOffset * profile.AverageTargetOffset));
            profile.PreferredSide = DescribeSide(profile);
            profile.PreferredHeight = DescribeHeight(profile.AverageTargetHeight);
            profile.PowerTrait = DescribePower(profile.AverageSpeed);
            profile.ConsistencyTrait = DescribeConsistency(profile.TargetSpread);
            return profile;
        }

        // A signature is built from parsed samples, not file names or text, so copies with
        // different whitespace, headers, or filenames are counted as one training shot.
        private static string CreateTrajectorySignature(BallTrajectory trajectory)
        {
            StringBuilder signature = new StringBuilder(trajectory.SampleCount * 42);
            for (int index = 0; index < trajectory.Samples.Count; index++)
            {
                TrajectorySample sample = trajectory.Samples[index];
                AppendFloat(signature, sample.Time);
                AppendFloat(signature, sample.CenterPosition.x);
                AppendFloat(signature, sample.CenterPosition.y);
                AppendFloat(signature, sample.CenterPosition.z);
            }
            return signature.ToString();
        }

        private static void AppendFloat(StringBuilder output, float value)
        {
            // CSV data normally has millimetre-level reconstruction precision. Rounding here
            // treats formatting-only differences as duplicates without merging distinct shots.
            output.Append((Mathf.Round(value * 1000f) / 1000f)
                .ToString("0.000", CultureInfo.InvariantCulture));
            output.Append('|');
        }

        private static string DescribeSide(ShooterProfile profile)
        {
            int max = Mathf.Max(profile.LeftCount, Mathf.Max(profile.CenterCount, profile.RightCount));
            if (max == profile.CenterCount)
                return "中路倾向";
            if (profile.LeftCount == profile.RightCount)
                return "左右均衡";
            // CSV positive Y is goalkeeper's right and shooter's left.
            return max == profile.LeftCount ? "偏射手左侧" : "偏射手右侧";
        }

        private static string DescribeHeight(float height)
        {
            if (height < 0.75f)
                return "低平球";
            if (height > 1.65f)
                return "高点冲击";
            return "中路半高球";
        }

        private static string DescribePower(float speed)
        {
            if (speed >= 19f)
                return "力量型射门";
            if (speed <= 12f)
                return "节奏型射门";
            return "均衡发力";
        }

        private static string DescribeConsistency(float spread)
        {
            if (spread <= 0.60f)
                return "落点稳定";
            if (spread >= 1.35f)
                return "落点多变";
            return "落点均衡";
        }
    }
}
