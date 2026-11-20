using Assets.SuperGoalie.Scripts.Entities;
using Assets.SuperGoalie.Scripts.FSMs;
using Assets.SuperGoalie.Scripts.Others.Utilities;
using Assets.SuperGoalie.Scripts.States.GoalKeeperStates.Dive.MainState;
using RobustFSM.Base;
using UnityEngine;

namespace Assets.SuperGoalie.Scripts.States.GoalKeeperStates.InterceptShot.MainState
{
    public class InterceptShotMainState : BState
    {
        float _height;
        float _initDistOfBallToOrthogonalPoint;
        float _speed;
        float _timeToOrthogonalPoint;
        float _timeOfBallToPlayerOrthogonalPointOnBallPath;
        float _timeOfBallToPlayerOrthogonalPointOnBallPathCached;
        float _turn;
        float _weightMultiplier;
        Vector3 _ballInitPosition;
        Vector3 _playerInterceptPoint;
        Vector3 _playerSteeringTarget;
        Vector3 _ballPositionAtPlayerOrthogonalPoint;
        Vector3 _ballVelocity;
        Vector3 _ballVelocityXZ;
        Vector3 _normalizedBallInitialPosition;
        Vector3 _normalizedBallPosition;
        Vector3 _normalizedBallTarget;
        Vector3 _normalizedPlayerPosition;
        Vector3 _relativeBallPositionAtPlayerInterceptPoint;
        Vector3 _orthogonalPointToPlayerPositionOnBallPath;

        public bool BallTrapable { get; set; }

        /// <summary>
        /// 是否因概率判定为扑救成功（即使几何上能碰到球）
        /// </summary>
        public bool ShouldSaveSucceed { get; set; }

        public float RequiredJumpHeight { get; set; }

        public Vector3 LeftHandTargetPosition { get; set; }

        public Vector3 RightHandTargetPosition { get; set; }

        float _effectiveReach;

        // ── 扑救动作的时间对齐 ────────────────────────────────────────────
        // 每个扑救动作全长 2.5–3.5 秒，手碰到球只在 18%–30% 处，之后是倒地与起身。
        // 所以不能等球到点才起播，而要反推「接触帧应该落在球到达的时刻」：
        //   contactLead = 接触点 × 动作时长        （起跳到碰球要多久）
        //   delay       = 球到点时间 − contactLead （原地准备多久再蹬地）
        // 若球太快（delay < 0），则原地起跳并加速播放，让接触帧仍压在球的到达时刻。
        string _diveState;
        GKDiveTiming.Dive _dive;
        float _diveDelay;
        float _diveSpeed = 1f;
        bool _diveStarted;

        // 起跳后的水平推进：动画的根位移只取垂直分量（用于腾空与落地），
        // 水平位移由这里按 ease-out 推进到拦截点——起跳瞬间爆发、接触前收住，
        // 不会像匀速平移那样在地上滑。
        Vector3 _interceptPoint;
        Vector3 _diveStartPos;
        float _diveElapsed;
        float _diveDuration = 0.5f;
        float _diveMoveSpeed = 3f;

        public string DiveState { get { return _diveState; } }
        public GKDiveTiming.Dive DiveInfo { get { return _dive; } }

        public override void Enter()
        {
            base.Enter();

            Owner.SaveAttemptSuccess = false;
            Owner.WasHitByBall = false;
            BallTrapable = false;

            Vector3 goalLineCenter = Owner.Goal != null ? Owner.Goal.CsvCoordinateOrigin : Owner.Position;
            Vector3 goalRight = Owner.Goal != null ? Owner.Goal.PitchRight : Owner.transform.right;
            Vector3 goalForward = Owner.Goal != null ? Owner.Goal.PitchForward : Owner.transform.forward;
            float goalWidth = Owner.Goal != null ? Owner.Goal.GoalWidth : 7.32f;
            float goalHeight = Owner.Goal != null ? Owner.Goal.GoalHeight : 2.44f;
            float halfWidth = goalWidth * 0.5f;
            float postMargin = Mathf.Max(0.18f, Owner.Ball.SphereCollider.radius + 0.08f);

            Vector3 ballAtGoalLocal = Owner.Goal != null
                ? Owner.Goal.WorldToGoalCoordinates(Owner.BallHitTarget)
                : Owner.transform.InverseTransformPoint(Owner.BallHitTarget);
            ShouldSaveSucceed = Owner.RollSaveAttempt(ballAtGoalLocal);

            float probabilityScale = Mathf.Lerp(0.70f, 1.25f, Mathf.Clamp01(Owner.SaveProbability));
            float keepingScale = Mathf.Lerp(0.65f, 1.22f, Mathf.Clamp01(Owner.GoalKeeping));
            float successScale = ShouldSaveSucceed ? 1f : 0.58f;
            _effectiveReach = Owner.Reach * probabilityScale * successScale;

            float sidePreference = Owner.GoalkeeperData != null ? Owner.GoalkeeperData.SidePreference : 0f;
            float heightPreference = Owner.GoalkeeperData != null ? Owner.GoalkeeperData.HeightPreference : 0f;
            float targetLateral = Mathf.Clamp(ballAtGoalLocal.x, -halfWidth + postMargin, halfWidth - postMargin);
            float currentLateral = Vector3.Dot(Owner.Position - goalLineCenter, goalRight);
            // SidePreference is authored from the shooter's view, opposite to the
            // keeper/world lateral axis used for physical movement.
            float biasedTargetLateral = Mathf.Clamp(targetLateral - sidePreference * halfWidth * 0.28f,
                -halfWidth + postMargin, halfWidth - postMargin);
            float anticipation = Mathf.Lerp(0.35f, 0.95f, Mathf.Clamp01(Owner.GoalKeeping)) * Mathf.Lerp(0.70f, 1.10f, Mathf.Clamp01(Owner.SaveProbability));
            if (!ShouldSaveSucceed)
                anticipation *= 0.62f;
            float desiredLateral = Mathf.Lerp(currentLateral, biasedTargetLateral, Mathf.Clamp01(anticipation));
            float maxDiveTravel = Owner.JumpDistance * probabilityScale * successScale;
            desiredLateral = Mathf.Clamp(
                desiredLateral,
                currentLateral - maxDiveTravel,
                currentLateral + maxDiveTravel);

            float forwardOffset = Mathf.Clamp(0.70f + Owner.TendGoalDistance * 0.30f, 1.05f, 1.75f);

            float distanceToIntercept = Vector3.Distance(Owner.Ball.CenterPosition, Owner.BallHitTarget);
            float currentBallSpeed = Mathf.Max(Owner.Ball.Velocity.magnitude, Owner.BallVelocity, 0.1f);
            _timeOfBallToPlayerOrthogonalPointOnBallPathCached = _timeOfBallToPlayerOrthogonalPointOnBallPath =
                Mathf.Clamp(distanceToIntercept / currentBallSpeed, 0.12f, 1.20f);
            _ballPositionAtPlayerOrthogonalPoint = Owner.Ball.FuturePosition(_timeOfBallToPlayerOrthogonalPointOnBallPath);
            _relativeBallPositionAtPlayerInterceptPoint = Owner.transform.InverseTransformPoint(_ballPositionAtPlayerOrthogonalPoint);

            float normalizedTargetHeight = Mathf.Clamp01(ballAtGoalLocal.y / Mathf.Max(0.1f, goalHeight));
            float heightBoost = Mathf.Lerp(0.75f, 1.20f, Mathf.Clamp01((heightPreference + 1f) * 0.5f));
            RequiredJumpHeight = Mathf.Clamp((normalizedTargetHeight * goalHeight) - Owner.Height * 0.55f,
                0f, Owner.JumpHeight * probabilityScale * successScale * heightBoost);

            float lateralDelta = targetLateral - currentLateral;
            if (Mathf.Abs(lateralDelta) < Mathf.Max(0.16f, _effectiveReach * 0.5f))
                _turn = 0f;
            else
                _turn = lateralDelta > 0f ? 1f : -1f;

            _height = normalizedTargetHeight;

            // 按「高度档 + 左右 + 需要横移多少」挑动作：各格动作自带的横向覆盖差很多
            //（0.24–2.01 m），拿 2 米的侧扑去接近角，人一定扑过头，看起来就假。
            _diveState = GKDiveTiming.SelectState(_turn, _height, Mathf.Abs(lateralDelta));
            _dive = GKDiveTiming.Get(_diveState);

            // 起跳点：动作自己会横向带出 _dive.LateralTravel 米，所以先走到
            //「目标点 − 动作自带位移」的位置。起跳后位移全程交给动画，
            // 代码不再平移，避免「代码平移 + 骨骼横移」叠加成滑行。
            float jumpLateral = targetLateral - _turn * Mathf.Min(_dive.LateralTravel, Mathf.Abs(lateralDelta));
            _playerSteeringTarget = goalLineCenter + goalRight * jumpLateral + goalForward * forwardOffset;
            _playerSteeringTarget.y = Owner.Position.y;
            _orthogonalPointToPlayerPositionOnBallPath = _playerSteeringTarget;

            _interceptPoint = goalLineCenter + goalRight * targetLateral + goalForward * forwardOffset;
            _interceptPoint.y = Owner.Position.y;

            float movementDistance = Vector3.ProjectOnPlane(_playerSteeringTarget - Owner.Position, Vector3.up).magnitude;
            float effectiveDiveSpeed = Owner.DiveSpeed * keepingScale * probabilityScale;
            float playerRawDiveSpeed = movementDistance / Mathf.Max(_timeOfBallToPlayerOrthogonalPointOnBallPath, Time.fixedDeltaTime);
            float playerDiveSpeed = Mathf.Clamp(playerRawDiveSpeed, Owner.TendGoalSpeed * 0.85f, effectiveDiveSpeed * 1.25f);

            // 接触帧对齐：让「手伸到最远」的那一帧正好落在球到达的时刻
            float contactLead = _dive.ContactNorm * _dive.Length;
            float tContact = _timeOfBallToPlayerOrthogonalPointOnBallPath;
            if (tContact >= contactLead)
            {
                _diveDelay = tContact - contactLead;
                _diveSpeed = 1f;
            }
            else
            {
                // 球太快，来不及从容起跳：立刻蹬地，并把播放速度提上去，
                // 让接触帧仍然压在球到达的那一刻，而不是球都过人了才开始飞。
                _diveDelay = 0f;
                _diveSpeed = Mathf.Clamp(contactLead / Mathf.Max(tContact, 0.08f), 1f, 2.4f);
            }

            // 原地准备阶段要赶到起跳点，横移速度按剩余时间反推
            if (_diveDelay > 0.02f && movementDistance > 0.05f)
                playerDiveSpeed = Mathf.Clamp(movementDistance / _diveDelay, playerDiveSpeed, Owner.DiveSpeed * 1.6f);

            Owner.RPGMovement.SetMoveTarget(_playerSteeringTarget);
            Owner.RPGMovement.Speed = playerDiveSpeed;
            Owner.RPGMovement.CurrentSpeed = Mathf.Max(Owner.RPGMovement.CurrentSpeed, playerDiveSpeed * 0.45f);
            Owner.RPGMovement.SetSteeringOn();
            Owner.RPGMovement.SetTrackingOn();
            Owner.RPGMovement.SetRotateFacePosition(Owner.Ball.Position);

            Owner.Animator.SetFloat("Height", _height);
            Owner.Animator.SetFloat("Turn", _turn);

            _diveStarted = _diveDelay <= 0f;
            if (_diveStarted)
                StartDive();

            Debug.Log($"[GKDecision] goalLocal={ballAtGoalLocal} Height={_height:F2} Turn={_turn:F2} need={Mathf.Abs(lateralDelta):F2}m => {_diveState} delay={_diveDelay:F2}s speed={_diveSpeed:F2}");

            LeftHandTargetPosition = GetBoneIKTarget(HumanBodyBones.LeftHand);
            RightHandTargetPosition = GetBoneIKTarget(HumanBodyBones.RightHand);
        }
        public override void Execute()
        {
            base.Execute();

            _normalizedBallPosition = Owner.Ball.CenterPosition;
            _normalizedPlayerPosition = new Vector3(Owner.Position.x, 0f, Owner.Position.z);

            if (!_diveStarted)
            {
                // 起跳前的原地准备：继续移动到起跳点，播的是站位/横移动作
                _diveDelay -= Time.deltaTime;

                float distanceToMoveTarget = Vector3.ProjectOnPlane(_playerSteeringTarget - Owner.Position, Vector3.up).magnitude;
                if (distanceToMoveTarget > 0.08f)
                {
                    Owner.RPGMovement.SetMoveTarget(_playerSteeringTarget);
                    Owner.RPGMovement.SetSteeringOn();
                }
                else
                {
                    Owner.RPGMovement.SetSteeringOff();
                }

                Vector3 relVelocity = Owner.transform.InverseTransformDirection(Owner.RPGMovement.Velocity);
                Owner.Animator.SetFloat("Forward", Mathf.Clamp(relVelocity.z, -1f, 1f), 0.08f, 0.08f);
                Owner.Animator.SetFloat("Turn", _turn, 0.08f, 0.08f);

                if (_diveDelay <= 0f)
                    StartDive();
            }
            else
            {
                // 起跳后：垂直交给动画 root motion，水平按 ease-out 推到拦截点。
                // 直接用 deltaPosition.xz 会因为动作自带 3–4 米的斜向位移而冲过头。
                _diveElapsed += Time.deltaTime;
                float progress = Mathf.Clamp01(_diveElapsed / _diveDuration);
                float eased = 1f - (1f - progress) * (1f - progress);
                Vector3 moveTarget = Vector3.Lerp(_diveStartPos, _interceptPoint, eased);
                moveTarget.y = Owner.Position.y;

                Owner.RPGMovement.SetMoveTarget(moveTarget);
                Owner.RPGMovement.Speed = _diveMoveSpeed;
                Owner.RPGMovement.SetSteeringOn();
            }

            Owner.RPGMovement.SetRotateFacePosition(Owner.Ball.Position);

            float frameDist = GetDistanceTravelledInSingleFrame(Owner.Ball.Velocity);
            float catchThreshold = Owner.Ball.WorldRadius + 0.11f + frameDist * 0.25f;
            if (_turn == 0f)
            {
                BallTrapable = GetDistOfBoneToPosition(HumanBodyBones.RightHand, _normalizedBallPosition) <= catchThreshold
                    || GetDistOfBoneToPosition(HumanBodyBones.LeftHand, _normalizedBallPosition) <= catchThreshold;
            }
            else if (_turn == 1f)
            {
                BallTrapable = GetDistOfBoneToPosition(HumanBodyBones.RightHand, _normalizedBallPosition) <= catchThreshold;
            }
            else
            {
                BallTrapable = GetDistOfBoneToPosition(HumanBodyBones.LeftHand, _normalizedBallPosition) <= catchThreshold;
            }

            _timeOfBallToPlayerOrthogonalPointOnBallPath -= Time.deltaTime;
            if (_timeOfBallToPlayerOrthogonalPointOnBallPath <= 0f)
                Machine.ChangeState<PunchBallMainState>();
        }
        public override void Exit()
        {
            base.Exit();

            //set the steering to off
            Owner.RPGMovement.SetSteeringOff();
            Owner.RPGMovement.SetTrackingOff();

            // 播放速度会被扑救阶段改动，退出时复位
            Owner.Animator.speed = 1f;

            //set the animator to exit the dive state
            Owner.Animator.ResetTrigger("Dive");
        }

        public override void OnAnimatorIK(int layerIndex)
        {
            base.OnAnimatorIK(layerIndex);

            //calculate the weight multiplier depending on the remaining distance to target
            _normalizedBallPosition = new Vector3(Owner.Ball.Position.x, 0f, Owner.Ball.Position.z);
            _normalizedPlayerPosition = new Vector3(Owner.Position.x, 0f, Owner.Position.z);

            //find the distance of ball to orthogonal point
            float distanceOfBallToTarget = Vector3.Distance(Owner.Ball.CenterPosition, _ballPositionAtPlayerOrthogonalPoint);

            //if ball comes within reach influence the weight multiplier
            float ikReach = Mathf.Max(0.1f, _effectiveReach);
            if (distanceOfBallToTarget > 5f * ikReach)
                _weightMultiplier = 0f;
            else
                _weightMultiplier = Mathf.Clamp01((5f * ikReach - distanceOfBallToTarget) / (5f * ikReach));

            float leftHandWeight = 0f;
            float rightHandWeight = 0f;
            float lookAtWeight = 0f;

            //choose which hands to effect
            if (_turn == 0f)
            {
                leftHandWeight = _weightMultiplier;
                rightHandWeight = _weightMultiplier;
                lookAtWeight = _weightMultiplier;
            }
            else if(_turn == -1)
            {
                leftHandWeight = _weightMultiplier;
                lookAtWeight = _weightMultiplier;
            }
            else if(_turn == 1)
            {
                rightHandWeight = _weightMultiplier;
                lookAtWeight = _weightMultiplier;
            }

            //set the animations weights
            Owner.Animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, leftHandWeight);
            Owner.Animator.SetIKPositionWeight(AvatarIKGoal.RightHand, rightHandWeight);
            Owner.Animator.SetLookAtWeight(lookAtWeight);

            //set the animations positions
            Owner.Animator.SetLookAtPosition(Owner.Ball.Position + new Vector3(0f, Owner.Ball.SphereCollider.radius, 0f));
            Owner.Animator.SetIKPosition(AvatarIKGoal.LeftHand, LeftHandTargetPosition);
            Owner.Animator.SetIKPosition(AvatarIKGoal.RightHand, RightHandTargetPosition);
        }

        public override void OnAnimatorMove()
        {
            base.OnAnimatorMove();

            // 垂直位移交给动画的 root motion。
            // 原来这里用 RequiredJumpHeight 手动 lerp，只会把人往上抬，
            // 永远不会下沉——扑地动作就变成横在空中。开启 applyRootMotion 后，
            // deltaPosition.y 才是真实的起跳/倒地/起身高度变化。
            if (!_diveStarted)
                return;

            Vector3 localPosition = Owner.ModelRoot.transform.localPosition;
            localPosition.y += Owner.Animator.deltaPosition.y;
            Owner.ModelRoot.transform.localPosition = localPosition;
        }

        public Transform GetBone(HumanBodyBones bone)
        {
            Transform boneTransform = Owner.Animator.GetBoneTransform(bone);
            return boneTransform != null ? boneTransform : Owner.transform;
        }

        public float GetDistOfBoneToPosition(HumanBodyBones bone, Vector3 position)
        {
            //find the distance between the bone and the target
            Transform boneTransform = Owner.Animator.GetBoneTransform(bone);
            if (boneTransform == null)
                return float.PositiveInfinity;  // 自定义 Avatar 缺少该骨骼映射时，视为不可触球
            return Vector3.Distance(boneTransform.position, position);
        }

        public float GetDistanceTravelledInSingleFrame(Vector3 velocity)
        {
            return velocity.magnitude * Time.deltaTime;
        }

        public Vector3 GetBoneIKTarget(HumanBodyBones bone)
        {
            //prepare data to calculate hit target
            Vector3 ballIKTarget = _ballPositionAtPlayerOrthogonalPoint + new Vector3(0f, Owner.Ball.SphereCollider.radius, 0f);
            //Vector3 bonePosition = GetBone(bone).position;
            //Vector3 directionOfIkTargetToBone = bonePosition - ballIKTarget;

            //calculate the ik target
            //ballIKTarget = bonePosition + directionOfIkTargetToBone.normalized * (directionOfIkTargetToBone.magnitude - Owner.Ball.SphereCollider.radius);
            //ballIKTarget = ballIKTarget + directionOfIkTargetToBone.normalized * Owner.Ball.SphereCollider.radius;

            //return the ik target
            return ballIKTarget;
        }

        /// <summary>
        /// 蹬地起跳：切到选定的扑救格，并按对齐算好的倍速播放。
        /// 起跳之后代码不再干预水平位移，横向幅度由动作本身负责。
        /// </summary>
        void StartDive()
        {
            if (_diveStarted)
                return;

            _diveStarted = true;
            _diveStartPos = Owner.Position;
            _diveElapsed = 0f;

            // 起跳到接触的这段就是水平推进的可用时间
            _diveDuration = Mathf.Max(_dive.ContactNorm * _dive.Length / Mathf.Max(_diveSpeed, 0.01f), 0.15f);
            float travelLeft = Vector3.ProjectOnPlane(_interceptPoint - _diveStartPos, Vector3.up).magnitude;
            _diveMoveSpeed = Mathf.Clamp(travelLeft / _diveDuration * 1.6f, 2.2f, 9f);

            Owner.Animator.speed = _diveSpeed;
            Owner.Animator.CrossFade(_diveState, 0.10f, 0, 0f);

            Debug.Log(string.Format("[GKDive] {0} speed={1:F2} contactLead={2:F2}s travel={3:F2}m",
                _diveState, _diveSpeed, _dive.ContactNorm * _dive.Length, _dive.LateralTravel));
        }

        GoalKeeper Owner
        {
            get
            {
                return ((GoalKeeperFSM)SuperMachine).Owner;
            }
        }

        public float Turn
        {
            get
            {
                return _turn;
            }

            set
            {
                _turn = value;
            }
        }
    }
}
