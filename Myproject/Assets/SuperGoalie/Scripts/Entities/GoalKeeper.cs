using Assets.SimpleSteering.Scripts.Movement;
using Assets.SuperGoalie.Scripts.Data;
using Assets.SuperGoalie.Scripts.FSMs;
using Assets.SuperGoalie.Scripts.States.GoalKeeperStates.Dive.MainState;
using Assets.SuperGoalie.Scripts.States.GoalKeeperStates.TendGoal.MainState;
using System;
using UnityEngine;

namespace Assets.SuperGoalie.Scripts.Entities
{
    [RequireComponent(typeof(GoalKeeperFSM))]
    [RequireComponent(typeof(RPGMovement))]
    public class GoalKeeper : MonoBehaviour
    {
        /// <summary>
        /// A reference to the dive speed of this instance
        /// </summary>
        [SerializeField]
        float _diveSpeed = 4f;

        /// <summary>
        /// A refernce to the goal keeping of this instance
        /// </summary>
        [SerializeField]
        float _goalKeeping = 0.85f;

        /// <summary>
        /// A reference to the height of this instance
        /// </summary>
        float _height = 1.9f;

        /// <summary>
        /// A refernce to the jump distance of this instance
        /// </summary>
        [SerializeField]
        float _jumpDistance = 1;

        /// <summary>
        /// A reference to the jump height of this instance
        /// </summary>
        [SerializeField]
        float _jumpHeight = 0.5f;

        /// <summary>
        /// A refernce to the goal keeping of this instance
        /// </summary>
        [SerializeField]
        float _reach = 0.5f;

        /// <summary>
        ///  reference to the tend goal distance of this instance
        /// </summary>
        [SerializeField]
        float _tendGoalDistance = 3f;

        /// <summary>
        ///  reference to the tend goal speed of this instance
        /// </summary>
        [SerializeField]
        float _tendGoalSpeed = 3f;

        /// <summary>
        /// A reference to this instance's animator
        /// </summary>
        [SerializeField]
        Animator _animator;

        /// <summary>
        /// A reference to the ball instance
        /// </summary>
        [SerializeField]
        Ball _ball;

        /// <summary>
        /// A reference to the goal instance
        /// </summary>
        [SerializeField]
        Goal _goal;

        /// <summary>
        /// A reference to the model root
        /// </summary>
        [SerializeField]
        Transform _modelRoot;

        [Header("门将数据库")]
        [SerializeField]
        GoalkeeperData _goalkeeperData;

        public Action OnHasNoBall;

        public Action OnHasBall;

        public Action OnPunchBall;

        /// <summary>
        /// 本次扑救是否成功（由概率系统判定）
        /// </summary>
        public bool SaveAttemptSuccess { get; set; }

        /// <summary>
        /// 球是否碰到了守门员身体（物理碰撞，非扑救）
        /// </summary>
        public bool WasHitByBall { get; set; }

        /// <summary>
        /// 本次扑救的预期成功率 (0-1)
        /// </summary>
        public float SaveProbability { get; set; }

        public delegate void BallLaunched(float flightPower, float velocity, Vector3 initial, Vector3 target);
        public BallLaunched OnBallLaunched;

        public bool HasBall { get; set; }

        public float BallFlightTime { get; set; }

        public Vector3 BallHitTarget { get; set; }

        public Vector3 BallInitialPosition { get; internal set; }

        public GoalKeeperFSM FSM { get; set; }

        public RPGMovement RPGMovement { get; set; }

        /// <summary>守门员初始位置（球门正中，球门线上）</summary>
        Vector3 _homePosition;
        Quaternion _homeRotation;

        struct BoneCapsule
        {
            public HumanBodyBones Start;
            public HumanBodyBones End;
            public float Radius;

            public BoneCapsule(HumanBodyBones start, HumanBodyBones end, float radius)
            {
                Start = start;
                End = end;
                Radius = radius;
            }
        }

        // The imported character has only one upright root capsule. During a dive the
        // rendered skeleton leaves that capsule, so use the animated limbs as the
        // authoritative contact shape for trajectory playback.
        static readonly BoneCapsule[] AnimatedContactCapsules =
        {
            new BoneCapsule(HumanBodyBones.Head, HumanBodyBones.Neck, 0.13f),
            new BoneCapsule(HumanBodyBones.Hips, HumanBodyBones.Spine, 0.20f),
            new BoneCapsule(HumanBodyBones.Spine, HumanBodyBones.Chest, 0.18f),
            new BoneCapsule(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, 0.11f),
            new BoneCapsule(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, 0.10f),
            new BoneCapsule(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, 0.11f),
            new BoneCapsule(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, 0.10f),
            new BoneCapsule(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, 0.13f),
            new BoneCapsule(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, 0.10f),
            new BoneCapsule(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, 0.13f),
            new BoneCapsule(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, 0.10f)
        };

        private void Awake()
        {
            FSM = GetComponent<GoalKeeperFSM>();
            RPGMovement = GetComponent<RPGMovement>();

            // 将初始位置设在球门线正中
            if (_goal != null && _goal.HasCompleteGoalMouth)
            {
                _homePosition = _goal.CsvCoordinateOrigin;
            }
            else
            {
                _homePosition = transform.position;
            }

            // 从 ScriptableObject 加载门将数据
            _homeRotation = transform.rotation;

            if (_goalkeeperData != null)
                _goalkeeperData.ApplyTo(this);

            // 自动给守门员身体加碰撞体，防止球穿模
            EnsureBodyCollider();

            if (_ball == null)
                _ball = FindObjectOfType<Ball>(true);
            if (_goal == null)
                _goal = FindObjectOfType<Goal>(true);
        }

        /// <summary>
        /// 确保守门员身上有碰撞体（优先用已有的，没有就加 CapsuleCollider）
        /// </summary>
        void EnsureBodyCollider()
        {
            // 如果已有 Collider（非 Trigger），不用加了
            foreach (var col in GetComponentsInChildren<Collider>())
                if (!col.isTrigger) return;

            // 守门员身体碰撞体（真实的物理碰撞，球碰到会弹开）
            if (_modelRoot != null)
            {
                CapsuleCollider cc = _modelRoot.gameObject.AddComponent<CapsuleCollider>();
                cc.height = Height;
                cc.radius = 0.3f;
                cc.center = new Vector3(0f, Height * 0.5f, 0f);
                cc.direction = 1;
            }
        }

        /// <summary>回到球门中间的初始位置</summary>
        public void ResetPosition()
        {
            transform.SetPositionAndRotation(_homePosition, _homeRotation);
            if (RPGMovement != null)
                RPGMovement.SetSteeringOff();
        }

        /// <summary>设置初始位置和朝向</summary>
        public void SetHomePose(Vector3 position, Quaternion rotation)
        {
            _homePosition = position;
            _homeRotation = rotation;
            transform.SetPositionAndRotation(position, rotation);
        }

        /// <summary>重置到初始位置</summary>
        public void ResetToHome()
        {
            transform.SetPositionAndRotation(_homePosition, _homeRotation);
            if (RPGMovement != null)
                RPGMovement.SetSteeringOff();
        }

        public void PrepareForShot()
        {
            if (FSM != null && FSM.ContainsState<TendGoalMainState>())
                FSM.ChangeState<TendGoalMainState>();
        }

        public void RegisterBallContact()
        {
            WasHitByBall = true;
            SaveAttemptSuccess = true;
            if (FSM != null && FSM.ContainsState<PunchBallMainState>())
                FSM.ChangeState<PunchBallMainState>();
        }

        public bool IsBallWithChasingDistance()
        {
            return DistanceOfBallToGoal() <= 20f;
        }

        public bool IsBallWithThreateningDistance()
        {
            return DistanceOfBallToGoal() <= 30f;
        }

        public bool IsShotOnTarget()
        {
            return _goal != null && _goal.IsPositionWithinGoalMouthFrustrum(BallHitTarget);
        }

        public float DistanceOfBallToGoal()
        {
            if (_ball == null || _goal == null)
                return float.PositiveInfinity;

            return Vector3.Distance(_ball.transform.position, _goal.transform.position);
        }

        public void Instance_OnBallLaunched(float flightTime, float velocity, Vector3 initial, Vector3 target)
        {
            BallLaunched temp = OnBallLaunched;
            if (temp != null)
                temp.Invoke(flightTime, velocity, initial, target);
        }

        public Vector3 Position
        {
            get
            {
                return transform.position;
            }
        }

        public float DiveReach
        {
            get
            {
                return JumpDistance + Reach;
            }
        }

        public float DiveSpeed
        {
            get
            {
                return _diveSpeed;
            }

            set
            {
                _diveSpeed = value;
            }
        }

        public float GoalKeeping
        {
            get
            {
                return _goalKeeping;
            }

            set
            {
                _goalKeeping = value;
            }
        }

        public float JumpDistance
        {
            get
            {
                return _jumpDistance;
            }

            set
            {
                _jumpDistance = value;
            }
        }

        public float JumpReach
        { 
            get
            {
                return Height + JumpHeight;
            }
        }

        public float Reach
        {
            get
            {
                return _reach;
            }

            set
            {
                _reach = value;
            }
        }

        public float TendGoalDistance
        {
            get
            {
                return _tendGoalDistance;
            }

            set
            {
                _tendGoalDistance = value;
            }
        }

        public float TendGoalSpeed
        {
            get
            {
                return _tendGoalSpeed;
            }

            set
            {
                _tendGoalSpeed = value;
            }
        }

        public Animator Animator
        {
            get
            {
                return _animator;
            }

            set
            {
                _animator = value;
            }
        }

        public Ball Ball
        {
            get
            {
                return _ball;
            }

            set
            {
                _ball = value;
            }
        }

        public Goal Goal
        {
            get
            {
                return _goal;
            }

            set
            {
                _goal = value;
            }
        }

        public float Height
        {
            get
            {
                return _height;
            }

            set
            {
                _height = value;
            }
        }

        public float JumpHeight
        {
            get
            {
                return _jumpHeight;
            }

            set
            {
                _jumpHeight = value;
            }
        }

        public Transform ModelRoot
        {
            get
            {
                return _modelRoot;
            }

            set
            {
                _modelRoot = value;
            }
        }

        public float BallVelocity { get; internal set; }

        /// <summary>
        /// 当前门将数据配置
        /// </summary>
        public GoalkeeperData GoalkeeperData
        {
            get { return _goalkeeperData; }
            set
            {
                _goalkeeperData = value;
                if (_goalkeeperData != null)
                    _goalkeeperData.ApplyTo(this);
            }
        }

        /// <summary>
        /// 从 JSON 文件加载并应用门将数据（运行时切换）
        /// </summary>
        public bool LoadGoalkeeperFromJson(string jsonPath)
        {
            var data = GoalkeeperData.LoadFromJson(jsonPath);
            if (data == null) return false;
            GoalkeeperData = data;
            return true;
        }

        /// <summary>
        /// 判断此球是否应该扑救成功（基于概率 + 随机）
        /// </summary>
        /// <param name="ballPositionAtGoal">球在球门本地坐标中的拦截位置</param>
        /// <returns>true=扑救成功, false=扑救失败但依然有动作</returns>
        public bool RollSaveAttempt(Vector3 ballPositionAtGoal)
        {
            if (_goalkeeperData == null)
            {
                SaveProbability = 0.85f;
            }
            else
            {
                float goalWidth = _goal != null ? _goal.GoalWidth : 7.32f;
                float goalHeight = _goal != null ? _goal.GoalHeight : 2.44f;
                // Goal/world +X is the keeper's right, while trajectory names and
                // probability-map Left/Right use the shooter's view. Those views are
                // mirrored, so convert only the data lookup axis, not world motion.
                Vector3 dataPosition = ballPositionAtGoal;
                dataPosition.x = -dataPosition.x;
                SaveProbability = _goalkeeperData.GetSaveProbability(dataPosition, goalWidth, goalHeight);

                // 应用偏好修正
                float nx = Mathf.InverseLerp(-goalWidth * 0.5f, goalWidth * 0.5f, dataPosition.x);
                float ny = Mathf.InverseLerp(0f, goalHeight, ballPositionAtGoal.y);
                SaveProbability += _goalkeeperData.SidePreference * (nx - 0.5f) * 0.1f;
                SaveProbability += _goalkeeperData.HeightPreference * (ny - 0.5f) * 0.1f;
                // Probability maps describe zone performance; GoalKeeping calibrates
                // the overall level without turning the map into a binary threshold.
                SaveProbability *= Mathf.Lerp(0.82f, 1.06f, Mathf.Clamp01(GoalKeeping));
                SaveProbability = Mathf.Clamp01(SaveProbability);
            }

            return UnityEngine.Random.value <= SaveProbability;
        }

        /// <summary>
        /// Sweeps the ball centre against the currently rendered humanoid skeleton.
        /// Returns the earliest contact, including the ball radius, so a fast kinematic
        /// trajectory cannot jump from one side of an animated limb to the other.
        /// </summary>
        public bool TryGetAnimatedContact(
            Vector3 startCenter,
            Vector3 endCenter,
            float ballRadius,
            out Vector3 contactCenter,
            out Vector3 contactNormal)
        {
            contactCenter = endCenter;
            contactNormal = Vector3.zero;
            if (_animator == null || !_animator.isHuman)
                return false;

            bool found = false;
            float earliest = 1f;
            for (int i = 0; i < AnimatedContactCapsules.Length; i++)
            {
                BoneCapsule capsule = AnimatedContactCapsules[i];
                Transform startBone = _animator.GetBoneTransform(capsule.Start);
                Transform endBone = _animator.GetBoneTransform(capsule.End);
                if (startBone == null || endBone == null)
                    continue;

                float hitFraction;
                Vector3 normal;
                if (!SweepPointAgainstCapsule(
                    startCenter,
                    endCenter,
                    startBone.position,
                    endBone.position,
                    capsule.Radius + ballRadius,
                    out hitFraction,
                    out normal))
                    continue;

                if (!found || hitFraction < earliest)
                {
                    found = true;
                    earliest = hitFraction;
                    contactNormal = normal;
                }
            }

            if (!found)
                return false;

            contactCenter = Vector3.Lerp(startCenter, endCenter, earliest);
            if (contactNormal.sqrMagnitude < 0.000001f)
                contactNormal = (startCenter - endCenter).normalized;
            return true;
        }

        public bool UsesAnimatedContactRig
        {
            get { return _animator != null && _animator.isHuman; }
        }

        static bool SweepPointAgainstCapsule(
            Vector3 pathStart,
            Vector3 pathEnd,
            Vector3 capsuleStart,
            Vector3 capsuleEnd,
            float radius,
            out float hitFraction,
            out Vector3 hitNormal)
        {
            float radiusSquared = radius * radius;
            Vector3 startClosest = ClosestPointOnSegment(pathStart, capsuleStart, capsuleEnd);
            if ((pathStart - startClosest).sqrMagnitude <= radiusSquared)
            {
                hitFraction = 0f;
                hitNormal = (pathStart - startClosest).normalized;
                return true;
            }

            float closestPathFraction;
            ClosestPointsOnSegments(
                pathStart, pathEnd, capsuleStart, capsuleEnd,
                out closestPathFraction);
            Vector3 closestPathPoint = Vector3.Lerp(pathStart, pathEnd, closestPathFraction);
            Vector3 closestCapsulePoint = ClosestPointOnSegment(closestPathPoint, capsuleStart, capsuleEnd);
            if ((closestPathPoint - closestCapsulePoint).sqrMagnitude > radiusSquared)
            {
                hitFraction = 0f;
                hitNormal = Vector3.zero;
                return false;
            }

            // Distance to a capsule is convex along a segment. Binary search between
            // the outside start and the closest point to find the first surface contact.
            float low = 0f;
            float high = closestPathFraction;
            for (int i = 0; i < 10; i++)
            {
                float mid = (low + high) * 0.5f;
                Vector3 point = Vector3.Lerp(pathStart, pathEnd, mid);
                Vector3 capsulePoint = ClosestPointOnSegment(point, capsuleStart, capsuleEnd);
                if ((point - capsulePoint).sqrMagnitude <= radiusSquared)
                    high = mid;
                else
                    low = mid;
            }

            hitFraction = high;
            Vector3 hitPoint = Vector3.Lerp(pathStart, pathEnd, hitFraction);
            Vector3 bonePoint = ClosestPointOnSegment(hitPoint, capsuleStart, capsuleEnd);
            hitNormal = (hitPoint - bonePoint).normalized;
            return true;
        }

        static Vector3 ClosestPointOnSegment(Vector3 point, Vector3 start, Vector3 end)
        {
            Vector3 segment = end - start;
            float lengthSquared = segment.sqrMagnitude;
            if (lengthSquared < 0.000001f)
                return start;
            float t = Mathf.Clamp01(Vector3.Dot(point - start, segment) / lengthSquared);
            return start + segment * t;
        }

        static void ClosestPointsOnSegments(
            Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2,
            out float firstSegmentFraction)
        {
            Vector3 d1 = q1 - p1;
            Vector3 d2 = q2 - p2;
            Vector3 r = p1 - p2;
            float a = Vector3.Dot(d1, d1);
            float e = Vector3.Dot(d2, d2);
            float f = Vector3.Dot(d2, r);
            float s;
            float t;

            if (a <= 0.000001f && e <= 0.000001f)
            {
                firstSegmentFraction = 0f;
                return;
            }
            if (a <= 0.000001f)
            {
                s = 0f;
                t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= 0.000001f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denominator = a * e - b * b;
                    s = Mathf.Abs(denominator) > 0.000001f
                        ? Mathf.Clamp01((b * f - c * e) / denominator)
                        : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = Mathf.Clamp01(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Mathf.Clamp01((b - c) / a);
                    }
                }
            }

            firstSegmentFraction = s;
        }
    }
}
