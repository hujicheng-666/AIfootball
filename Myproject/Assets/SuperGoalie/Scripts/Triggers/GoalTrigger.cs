using Assets.SuperGoalie.Scripts.Entities;
using System;
using UnityEngine;

namespace Assets.SuperGoalie.Scripts.Triggers
{
    /// <summary>
    /// FIFA-style goal detection: the ball must fully cross the goal line from the pitch side.
    /// The calculation uses the Goal coordinate system, not this trigger object's transform.
    /// </summary>
    public class GoalTrigger : MonoBehaviour
    {
        public Action OnCollidedWithBall;
        public float GoalLineX = 0f;

        const float MinGoalwardSpeed = 0.05f;

        Ball _ball;
        Goal _goal;
        bool _goalScored;
        float _previousDistanceIntoPitch = float.PositiveInfinity;

        void Start()
        {
            _ball = FindObjectOfType<Ball>(true);
            _goal = GetComponentInParent<Goal>();
            if (_goal == null)
                _goal = FindObjectOfType<Goal>(true);
        }

        void FixedUpdate()
        {
            if (_goalScored)
                return;
            if (_ball == null)
                _ball = FindObjectOfType<Ball>(true);
            if (_ball == null)
                return;
            if (_goal == null)
                _goal = GetComponentInParent<Goal>() ?? FindObjectOfType<Goal>(true);

            Vector3 ballCenter = _ball.CenterPosition;
            float ballRadius = GetWorldBallRadius();
            Vector3 pitchForward = GetPitchForward();
            Vector3 goalLineCenter = _goal != null ? _goal.CsvCoordinateOrigin : transform.position;

            float distanceIntoPitch = Vector3.Dot(ballCenter - goalLineCenter, pitchForward) - GoalLineX;
            float velocityIntoPitch = Vector3.Dot(_ball.Velocity, pitchForward);
            bool insideGoalMouth = _goal == null || _goal.IsPositionWithinGoalMouthFrustrum(ballCenter);
            bool fullyBehindLine = distanceIntoPitch < -ballRadius;
            bool crossedFromPitchSide = _previousDistanceIntoPitch >= -ballRadius && fullyBehindLine;
            bool movingTowardNet = velocityIntoPitch < -MinGoalwardSpeed;

            if (insideGoalMouth && crossedFromPitchSide && movingTowardNet)
            {
                _goalScored = true;
                Action temp = OnCollidedWithBall;
                if (temp != null)
                    temp.Invoke();
            }

            _previousDistanceIntoPitch = distanceIntoPitch;
        }

        public void ResetForNewShot()
        {
            _goalScored = false;
            _previousDistanceIntoPitch = float.PositiveInfinity;
        }

        float GetWorldBallRadius()
        {
            if (_ball == null || _ball.SphereCollider == null)
                return 0.11f;

            Vector3 scale = _ball.SphereCollider.transform.lossyScale;
            float largestScale = Mathf.Max(Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y)), Mathf.Abs(scale.z));
            return _ball.SphereCollider.radius * Mathf.Max(0.01f, largestScale);
        }

        Vector3 GetPitchForward()
        {
            Transform source = _goal != null ? _goal.transform : transform;
            Vector3 forward = Vector3.ProjectOnPlane(source.forward, Vector3.up);
            return forward.sqrMagnitude > Mathf.Epsilon ? forward.normalized : Vector3.forward;
        }

        void OnDisable() { }
    }
}