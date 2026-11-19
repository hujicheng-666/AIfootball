#if UNITY_INCLUDE_TESTS
using System.Collections.Generic;
using Assets.SuperGoalie.Scripts.Trajectories;
using NUnit.Framework;
using UnityEngine;

namespace Assets.SuperGoalie.Scripts.Editor
{
    /// <summary>
    /// Lightweight regression guards for the trajectory timing assumptions used by
    /// goalkeeper planning. Scene-level save/contact cases stay in Play Mode tests.
    /// </summary>
    public class GoalkeeperTrajectoryRegressionTests
    {
        [Test]
        public void FastNearPostTrajectory_PreservesInterpolatedTimeAndVelocity()
        {
            var trajectory = new BallTrajectory(new List<TrajectorySample>
            {
                new TrajectorySample(0f, new Vector3(0f, 1f, 5f)),
                new TrajectorySample(0.10f, new Vector3(1.5f, 1f, 2.5f)),
                new TrajectorySample(0.20f, new Vector3(3.0f, 1f, 0f)),
            });

            Assert.That(Vector3.Distance(trajectory.EvaluateCenter(0.15f), new Vector3(2.25f, 1f, 1.25f)), Is.LessThan(0.0001f));
            Assert.That(Vector3.Distance(trajectory.EvaluateVelocity(0.15f), new Vector3(15f, 0f, -25f)), Is.LessThan(0.0001f));
        }

        [Test]
        public void HighBallTrajectory_ReportsPeakAndClampsAtEnds()
        {
            var trajectory = new BallTrajectory(new List<TrajectorySample>
            {
                new TrajectorySample(0f, new Vector3(0f, 0.11f, 8f)),
                new TrajectorySample(0.25f, new Vector3(0f, 2.5f, 4f)),
                new TrajectorySample(0.50f, new Vector3(0f, 1.2f, 0f)),
            });

            Assert.That(trajectory.MaxCenterY, Is.EqualTo(2.5f));
            Assert.That(Vector3.Distance(trajectory.EvaluateCenter(-1f), trajectory.InitialCenter), Is.LessThan(0.0001f));
            Assert.That(Vector3.Distance(trajectory.EvaluateCenter(10f), trajectory.FinalCenter), Is.LessThan(0.0001f));
        }
    }
}
#endif
