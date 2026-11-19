using Assets.SuperGoalie.Scripts.Trajectories;
using System;
using UnityEngine;

namespace Assets.SuperGoalie.Scripts.Entities
{
    [RequireComponent(typeof(Rigidbody))]
    [RequireComponent(typeof(SphereCollider))]
    public class Ball : MonoBehaviour
    {
        [Tooltip("The gravity acting on the ball")]
        public float gravity = 9f;

        public delegate void BallLaunched(float flightTime, float velocity, Vector3 initial, Vector3 target);
        public BallLaunched OnBallLaunched;

        public Action OnTrajectoryCompleted;
        public Action OnTrajectoryReleased;

        public Rigidbody Rigidbody { get; set; }
        public SphereCollider SphereCollider { get; set; }
        public BallTrajectoryPlayer TrajectoryPlayer { get; private set; }

        private void Awake()
        {
            if (gameObject.tag != "Ball")
                gameObject.tag = "Ball";

            Rigidbody = GetComponent<Rigidbody>();
            SphereCollider = GetComponent<SphereCollider>();

            TrajectoryPlayer = GetComponent<BallTrajectoryPlayer>();
            if (TrajectoryPlayer == null)
                TrajectoryPlayer = gameObject.AddComponent<BallTrajectoryPlayer>();

            TrajectoryPlayer.Initialize(this);
            TrajectoryPlayer.Completed += Instance_OnTrajectoryCompleted;
            TrajectoryPlayer.ReleasedToPhysics += Instance_OnTrajectoryReleased;

            ConfigurePhysicsBody();
            Physics.gravity = new Vector3(0f, -gravity, 0f);
        }

        void ConfigurePhysicsBody()
        {
            Rigidbody.mass = Mathf.Max(0.43f, Rigidbody.mass);
            Rigidbody.drag = 0.08f;
            Rigidbody.angularDrag = 0.04f;
            Rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            Rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            if (SphereCollider.sharedMaterial == null)
            {
                PhysicMaterial material = new PhysicMaterial("BallPhysicMaterial");
                material.dynamicFriction = 0.35f;
                material.staticFriction = 0.35f;
                material.bounciness = 0.45f;
                material.frictionCombine = PhysicMaterialCombine.Average;
                material.bounceCombine = PhysicMaterialCombine.Maximum;
                SphereCollider.sharedMaterial = material;
            }
        }

        public void Stop()
        {
            StopRigidbodyOnly();
        }

        public void StopRigidbodyOnly()
        {
            Rigidbody.angularVelocity = Vector3.zero;
            Rigidbody.velocity = Vector3.zero;
        }

        public Vector3 FuturePosition(float time)
        {
            if (TrajectoryPlayer != null && TrajectoryPlayer.IsPlaying)
                return TrajectoryPlayer.EvaluateRootPosition(time);

            Vector3 velocity = Rigidbody.velocity;
            Vector3 velocityXZ = velocity;
            velocityXZ.y = 0f;

            float futurePositionY = Position.y + (velocity.y * time + 0.5f * -gravity * Mathf.Pow(time, 2));
            Vector3 futurePositionXZ = Position + velocityXZ * time;
            Vector3 futurePosition = futurePositionXZ;
            futurePosition.y = futurePositionY;
            return futurePosition;
        }

        public void Launch(float power, Vector3 final)
        {
            if (TrajectoryPlayer != null)
                TrajectoryPlayer.CancelAndUsePhysics();

            Vector3 initial = Position;
            Vector3 toTarget = final - initial;
            Vector3 toTargetXZ = toTarget;
            toTargetXZ.y = 0;

            // 目标在正上方/正下方（水平距离为 0）或 power 非法时防止除零，避免 NaN 速度
            if (power <= 0f)
            {
                Rigidbody.velocity = Vector3.zero;
                BallLaunched earlyHandler = OnBallLaunched;
                if (earlyHandler != null) earlyHandler.Invoke(0f, 0f, initial, final);
                return;
            }

            float time = toTargetXZ.magnitude / power;
            time = Mathf.Max(time, 0.01f);
            toTargetXZ = toTargetXZ.normalized * toTargetXZ.magnitude / time;

            Vector3 velocity = toTargetXZ;
            velocity.y = toTarget.y / time + (0.5f * gravity * time);
            Rigidbody.velocity = velocity;

            BallLaunched handler = OnBallLaunched;
            if (handler != null)
                handler.Invoke(time, power, initial, final);
        }

        public void Instance_OnBallLaunch(float power, Vector3 target)
        {
            Launch(power, target);
        }

        public void PlayTrajectory(BallTrajectory trajectory, Vector3 goalkeeperTargetCenter)
        {
            if (trajectory == null)
                throw new ArgumentNullException("trajectory");

            TrajectoryPlayer.Play(trajectory);

            float averageSpeed = Mathf.Max(trajectory.AverageSpeed, 0.1f);
            Vector3 initialRoot = RootPositionForCenter(trajectory.InitialCenter);
            Vector3 targetRoot = RootPositionForCenter(goalkeeperTargetCenter);

            BallLaunched handler = OnBallLaunched;
            if (handler != null)
                handler.Invoke(trajectory.Duration, averageSpeed, initialRoot, targetRoot);
        }

        public void HoldAtCenter(Vector3 worldCenter)
        {
            TrajectoryPlayer.HoldAtCenter(worldCenter);
        }

        public void CancelTrajectory()
        {
            TrajectoryPlayer.CancelAndUsePhysics();
        }

        public void ReleaseTrajectoryToPhysics(Vector3 releaseVelocity)
        {
            if (TrajectoryPlayer != null && (TrajectoryPlayer.IsPlaying || TrajectoryPlayer.IsHolding))
                TrajectoryPlayer.ReleaseToPhysics(releaseVelocity);
            else
                Rigidbody.velocity = releaseVelocity;
        }

        public Vector3 RootPositionForCenter(Vector3 worldCenter)
        {
            return worldCenter - transform.TransformVector(SphereCollider.center);
        }

        void Instance_OnTrajectoryCompleted()
        {
            Action handler = OnTrajectoryCompleted;
            if (handler != null)
                handler.Invoke();
        }

        void Instance_OnTrajectoryReleased()
        {
            Action handler = OnTrajectoryReleased;
            if (handler != null)
                handler.Invoke();
        }

        void OnCollisionEnter(Collision collision)
        {
            if (TrajectoryPlayer == null || !TrajectoryPlayer.IsPlaying)
                return;

            GoalKeeper keeper = collision.collider.GetComponentInParent<GoalKeeper>();
            if (keeper != null)
            {
                Vector3 normal = collision.contactCount > 0 ? collision.contacts[0].normal : -Velocity.normalized;
                TryDeflectFromKeeper(keeper, normal);
                return;
            }

            bool isGoalPart = collision.collider.name.Contains("Goal")
                           || collision.collider.name.Contains("goal")
                           || collision.collider.name.Contains("Post")
                           || collision.collider.name.Contains("post")
                           || collision.collider.name.Contains("Net")
                           || collision.collider.name.Contains("net")
                           || collision.collider.name.Contains("Bar")
                           || collision.collider.name.Contains("bar")
                           || collision.collider.GetComponentInParent<Goal>() != null;

            if (isGoalPart)
            {
                Vector3 incomingVelocity = Velocity;
                Vector3 normal = collision.contactCount > 0 ? collision.contacts[0].normal : -incomingVelocity.normalized;
                Vector3 releaseVelocity = Vector3.Reflect(incomingVelocity, normal) * 0.3f + Vector3.up * 1f;
                ReleaseTrajectoryToPhysics(releaseVelocity);
            }
        }

        public bool TryDeflectFromKeeper(GoalKeeper keeper, Vector3 contactNormal)
        {
            return TryDeflectFromKeeper(keeper, contactNormal, CenterPosition, GoalKeeper.KeeperContactKind.Body);
        }

        public bool TryDeflectFromKeeper(GoalKeeper keeper, Vector3 contactNormal, Vector3 ballCenter)
        {
            return TryDeflectFromKeeper(keeper, contactNormal, ballCenter, GoalKeeper.KeeperContactKind.Body);
        }

        public bool TryDeflectFromKeeper(
            GoalKeeper keeper,
            Vector3 contactNormal,
            Vector3 ballCenter,
            GoalKeeper.KeeperContactKind contactKind)
        {
            if (keeper == null)
                return false;

            Goal goal = keeper.Goal;
            float ballRadius = WorldRadius;
            if (IsFullyBehindGoalLine(goal, ballRadius, ballCenter))
                return false;

            Vector3 incomingVelocity = Velocity;
            Vector3 pitchForward = goal != null ? goal.PitchForward : Vector3.zero;
            Vector3 releaseDirection;
            float speedMultiplier;
            float lift;
            if (pitchForward.sqrMagnitude > Mathf.Epsilon)
            {
                Vector3 lateralNudge = Vector3.ProjectOnPlane(contactNormal, Vector3.up) * 0.25f;
                if (contactKind == GoalKeeper.KeeperContactKind.Hand)
                {
                    // A hand contact is the most controlled: direct the ball away
                    // from goal with enough lift to make a follow-up possible.
                    releaseDirection = pitchForward + lateralNudge * 0.55f + Vector3.up * 0.34f;
                    speedMultiplier = 0.60f;
                    lift = 0.9f;
                }
                else if (contactKind == GoalKeeper.KeeperContactKind.Arm)
                {
                    releaseDirection = pitchForward + lateralNudge * 1.20f + Vector3.up * 0.20f;
                    speedMultiplier = 0.46f;
                    lift = 0.7f;
                }
                else
                {
                    // Torso/leg blocks are less controlled and retain more of the
                    // incoming direction, which can produce rebounds near goal.
                    releaseDirection = Vector3.Reflect(incomingVelocity, contactNormal) + pitchForward * 0.45f + Vector3.up * 0.12f;
                    speedMultiplier = 0.34f;
                    lift = 0.45f;
                }
            }
            else
            {
                Vector3 fallbackNormal = contactNormal.sqrMagnitude > Mathf.Epsilon ? contactNormal.normalized : -incomingVelocity.normalized;
                releaseDirection = Vector3.Reflect(incomingVelocity, fallbackNormal);
                speedMultiplier = contactKind == GoalKeeper.KeeperContactKind.Hand ? 0.60f : 0.42f;
                lift = contactKind == GoalKeeper.KeeperContactKind.Hand ? 0.9f : 0.55f;
            }

            if (releaseDirection.sqrMagnitude < Mathf.Epsilon)
                releaseDirection = keeper.transform.forward;
            releaseDirection.Normalize();

            keeper.RegisterBallContact(contactKind);
            ReleaseTrajectoryToPhysics(releaseDirection * Mathf.Max(3.5f, incomingVelocity.magnitude * speedMultiplier) + Vector3.up * lift);
            return true;
        }

        bool IsFullyBehindGoalLine(Goal goal, float ballRadius, Vector3 ballCenter)
        {
            if (goal == null)
                return false;

            float distanceIntoPitch = Vector3.Dot(ballCenter - goal.CsvCoordinateOrigin, goal.PitchForward);
            return distanceIntoPitch < -ballRadius;
        }

        public Quaternion Rotation
        {
            get { return transform.rotation; }
            set { transform.rotation = value; }
        }

        public Vector3 Position
        {
            get { return transform.position; }
            set { transform.position = value; }
        }

        public Vector3 CenterPosition
        {
            get { return transform.TransformPoint(SphereCollider.center); }
        }

        public float WorldRadius
        {
            get
            {
                if (SphereCollider == null)
                    return 0.11f;

                Vector3 scale = SphereCollider.transform.lossyScale;
                float largestScale = Mathf.Max(Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y)), Mathf.Abs(scale.z));
                return SphereCollider.radius * Mathf.Max(0.01f, largestScale);
            }
        }

        public Vector3 Velocity
        {
            get
            {
                if (TrajectoryPlayer != null && TrajectoryPlayer.IsPlaying)
                    return TrajectoryPlayer.CurrentVelocity;
                return Rigidbody.velocity;
            }
        }

        public float TrajectoryTime
        {
            get { return TrajectoryPlayer != null ? TrajectoryPlayer.ElapsedTime : 0f; }
        }

        public float TrajectoryDuration
        {
            get { return TrajectoryPlayer != null ? TrajectoryPlayer.Duration : 0f; }
        }

        /// <summary>回放速度倍率（1 = 实时），转发给轨迹播放器</summary>
        public float PlaybackSpeed
        {
            get { return TrajectoryPlayer != null ? TrajectoryPlayer.PlaybackSpeed : 1f; }
            set { if (TrajectoryPlayer != null) TrajectoryPlayer.PlaybackSpeed = value; }
        }

        void OnDestroy()
        {
            if (TrajectoryPlayer == null)
                return;

            TrajectoryPlayer.Completed -= Instance_OnTrajectoryCompleted;
            TrajectoryPlayer.ReleasedToPhysics -= Instance_OnTrajectoryReleased;
        }
    }
}
