using Assets.SuperGoalie.Scripts.Entities;
using System;
using UnityEngine;

namespace Assets.SuperGoalie.Scripts.Trajectories
{
    /// <summary>
    /// Plays the recorded trajectory kinematically. A save releases the ball back to
    /// normal Rigidbody physics so the goalkeeper can deflect it.
    /// </summary>
    public sealed class BallTrajectoryPlayer : MonoBehaviour
    {
        Ball _ball;
        BallTrajectory _trajectory;
        Vector3 _currentVelocity;
        float _elapsedTime;
        bool _playing;
        GoalKeeper[] _keepers = new GoalKeeper[0];
        readonly Collider[] _overlapHits = new Collider[16];

        public event Action Completed;
        public event Action ReleasedToPhysics;

        public bool IsPlaying { get { return _playing; } }

        public bool IsHolding { get; private set; }

        public float ElapsedTime { get { return _elapsedTime; } }

        public float Duration { get { return _trajectory != null ? _trajectory.Duration : 0f; } }

        public Vector3 CurrentVelocity { get { return _playing ? _currentVelocity : Vector3.zero; } }

        public BallTrajectory ActiveTrajectory { get { return _trajectory; } }

        public void Initialize(Ball ball)
        {
            _ball = ball;
        }

        public void HoldAtCenter(Vector3 worldCenter)
        {
            EnsureInitialized();
            _playing = false;
            IsHolding = true;
            _trajectory = null;
            _elapsedTime = 0f;
            _currentVelocity = Vector3.zero;

            PrepareKinematicBody();
            _ball.Rigidbody.position = _ball.RootPositionForCenter(worldCenter);
        }

        public void Play(BallTrajectory trajectory)
        {
            EnsureInitialized();
            if (trajectory == null)
                throw new ArgumentNullException("trajectory");

            _trajectory = trajectory;
            _elapsedTime = 0f;
            _currentVelocity = trajectory.EvaluateVelocity(0f);
            IsHolding = false;
            _playing = true;
            _keepers = FindObjectsOfType<GoalKeeper>(true);

            PrepareKinematicBody();
            _ball.Rigidbody.position = _ball.RootPositionForCenter(trajectory.InitialCenter);
        }

        public Vector3 EvaluateRootPosition(float futureSeconds)
        {
            if (_trajectory == null)
                return _ball.Position;

            Vector3 center = _trajectory.EvaluateCenter(_elapsedTime + Mathf.Max(0f, futureSeconds));
            return _ball.RootPositionForCenter(center);
        }

        public void ReleaseToPhysics(Vector3 releaseVelocity)
        {
            EnsureInitialized();
            bool wasPlaying = _playing;

            _playing = false;
            IsHolding = false;
            _currentVelocity = releaseVelocity;
            _ball.Rigidbody.isKinematic = false;
            _ball.Rigidbody.useGravity = true;
            _ball.Rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _ball.Rigidbody.velocity = releaseVelocity;

            if (wasPlaying)
            {
                Action handler = ReleasedToPhysics;
                if (handler != null)
                    handler.Invoke();
            }
        }

        public void CancelAndUsePhysics()
        {
            EnsureInitialized();
            _playing = false;
            IsHolding = false;
            _trajectory = null;
            _elapsedTime = 0f;
            _currentVelocity = Vector3.zero;
            _ball.Rigidbody.isKinematic = false;
            _ball.Rigidbody.useGravity = true;
            _ball.Rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }

        void FixedUpdate()
        {
            if (!_playing || _trajectory == null)
                return;

            _elapsedTime = Mathf.Min(_elapsedTime + Time.fixedDeltaTime, _trajectory.Duration);
            Vector3 previousCenter = _ball.CenterPosition;
            Vector3 center = _trajectory.EvaluateCenter(_elapsedTime);
            _currentVelocity = _trajectory.EvaluateVelocity(_elapsedTime);

            if (TryDeflectAlongPath(previousCenter, center))
                return;

            _ball.Rigidbody.MovePosition(_ball.RootPositionForCenter(center));

            if (_elapsedTime >= _trajectory.Duration)
            {
                ReleaseToPhysics(_currentVelocity);
                Action handler = Completed;
                if (handler != null)
                    handler.Invoke();
            }
        }

        bool TryDeflectAlongPath(Vector3 startCenter, Vector3 endCenter)
        {
            float radius = Mathf.Max(0.08f, _ball.WorldRadius);
            GoalKeeper selectedKeeper = null;
            Vector3 selectedCenter = endCenter;
            Vector3 selectedNormal = Vector3.zero;
            float selectedDistance = float.PositiveInfinity;
            GoalKeeper.KeeperContactKind selectedContactKind = GoalKeeper.KeeperContactKind.Body;

            // The rendered humanoid bones are the contact authority. The prefab's
            // single upright capsule does not follow a diving animation and caused
            // both invisible saves and visible tunnelling through arms.
            if (_keepers == null || _keepers.Length == 0)
                _keepers = FindObjectsOfType<GoalKeeper>(true);
            for (int i = 0; i < _keepers.Length; i++)
            {
                GoalKeeper keeper = _keepers[i];
                if (keeper == null)
                    continue;

                Vector3 contactCenter;
                Vector3 contactNormal;
                GoalKeeper.KeeperContactKind contactKind;
                if (keeper.TryGetAnimatedContact(startCenter, endCenter, radius, out contactCenter, out contactNormal, out contactKind))
                {
                    float distance = Vector3.Distance(startCenter, contactCenter);
                    if (distance < selectedDistance)
                    {
                        selectedDistance = distance;
                        selectedKeeper = keeper;
                        selectedCenter = contactCenter;
                        selectedNormal = contactNormal;
                        selectedContactKind = contactKind;
                    }
                }
            }

            // Non-humanoid fallback for any custom keeper that cannot expose bones.
            if (selectedKeeper == null)
            {
                int hitCount = Physics.OverlapSphereNonAlloc(
                    endCenter, radius, _overlapHits, ~0, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < hitCount; i++)
                {
                    GoalKeeper keeper = _overlapHits[i].GetComponentInParent<GoalKeeper>();
                    if (keeper == null || keeper.UsesAnimatedContactRig)
                        continue;
                    selectedKeeper = keeper;
                    selectedCenter = endCenter;
                    Vector3 closest = _overlapHits[i].ClosestPoint(endCenter);
                    selectedNormal = (endCenter - closest).normalized;
                    break;
                }
            }

            if (selectedKeeper == null)
                return false;

            if (selectedNormal.sqrMagnitude < 0.000001f)
                selectedNormal = -_currentVelocity.normalized;

            // Put the ball exactly at the first visual contact before releasing it.
            // This keeps the deflection frame and the reported save frame identical.
            _ball.Rigidbody.position = _ball.RootPositionForCenter(
                selectedCenter + selectedNormal.normalized * 0.003f);
            return _ball.TryDeflectFromKeeper(
                selectedKeeper, selectedNormal.normalized, selectedCenter, selectedContactKind);
        }

        void PrepareKinematicBody()
        {
            _ball.StopRigidbodyOnly();
            _ball.Rigidbody.useGravity = false;
            _ball.Rigidbody.isKinematic = true;
            _ball.Rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }

        void EnsureInitialized()
        {
            if (_ball == null)
                _ball = GetComponent<Ball>();
            if (_ball == null)
                throw new InvalidOperationException("BallTrajectoryPlayer must be attached to the same object as Ball.");
        }
    }
}
