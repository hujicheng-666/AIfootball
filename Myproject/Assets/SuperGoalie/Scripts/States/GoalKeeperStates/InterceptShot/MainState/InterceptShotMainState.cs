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
            _playerSteeringTarget = goalLineCenter + goalRight * desiredLateral + goalForward * forwardOffset;
            _playerSteeringTarget.y = Owner.Position.y;

            float distanceToIntercept = Vector3.Distance(Owner.Ball.CenterPosition, Owner.BallHitTarget);
            float currentBallSpeed = Mathf.Max(Owner.Ball.Velocity.magnitude, Owner.BallVelocity, 0.1f);
            _timeOfBallToPlayerOrthogonalPointOnBallPathCached = _timeOfBallToPlayerOrthogonalPointOnBallPath =
                Mathf.Clamp(distanceToIntercept / currentBallSpeed, 0.12f, 1.20f);
            _ballPositionAtPlayerOrthogonalPoint = Owner.Ball.FuturePosition(_timeOfBallToPlayerOrthogonalPointOnBallPath);
            _relativeBallPositionAtPlayerInterceptPoint = Owner.transform.InverseTransformPoint(_ballPositionAtPlayerOrthogonalPoint);
            _orthogonalPointToPlayerPositionOnBallPath = _playerSteeringTarget;

            float movementDistance = Vector3.ProjectOnPlane(_playerSteeringTarget - Owner.Position, Vector3.up).magnitude;
            float effectiveDiveSpeed = Owner.DiveSpeed * keepingScale * probabilityScale;
            float playerRawDiveSpeed = movementDistance / Mathf.Max(_timeOfBallToPlayerOrthogonalPointOnBallPath, Time.fixedDeltaTime);
            float playerDiveSpeed = Mathf.Clamp(playerRawDiveSpeed, Owner.TendGoalSpeed * 0.85f, effectiveDiveSpeed * 1.25f);

            float normalizedTargetHeight = Mathf.Clamp01(ballAtGoalLocal.y / Mathf.Max(0.1f, goalHeight));
            float heightBoost = Mathf.Lerp(0.75f, 1.20f, Mathf.Clamp01((heightPreference + 1f) * 0.5f));
            RequiredJumpHeight = Mathf.Clamp((normalizedTargetHeight * goalHeight) - Owner.Height * 0.55f,
                0f, Owner.JumpHeight * probabilityScale * successScale * heightBoost);

            float lateralDelta = targetLateral - currentLateral;
            if (Mathf.Abs(lateralDelta) < Mathf.Max(0.16f, _effectiveReach * 0.5f))
                _turn = 0f;
            else
                _turn = lateralDelta > 0f ? 1f : -1f;

            Owner.RPGMovement.SetMoveTarget(_playerSteeringTarget);
            Owner.RPGMovement.Speed = playerDiveSpeed;
            Owner.RPGMovement.CurrentSpeed = Mathf.Max(Owner.RPGMovement.CurrentSpeed, playerDiveSpeed * 0.45f);
            Owner.RPGMovement.SetSteeringOn();
            Owner.RPGMovement.SetTrackingOn();
            Owner.RPGMovement.SetRotateFacePosition(Owner.Ball.Position);

            _height = Mathf.Clamp01(ballAtGoalLocal.y / Mathf.Max(0.1f, goalHeight));
            Owner.Animator.SetFloat("Height", _height);
            Owner.Animator.SetFloat("Turn", _turn);
            Owner.Animator.SetTrigger("Dive");

            LeftHandTargetPosition = GetBoneIKTarget(HumanBodyBones.LeftHand);
            RightHandTargetPosition = GetBoneIKTarget(HumanBodyBones.RightHand);
        }
        public override void Execute()
        {
            base.Execute();

            _normalizedBallPosition = Owner.Ball.CenterPosition;
            _normalizedPlayerPosition = new Vector3(Owner.Position.x, 0f, Owner.Position.z);

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
            Owner.RPGMovement.SetRotateFacePosition(Owner.Ball.Position);

            Vector3 relativeVelocity = Owner.transform.InverseTransformDirection(Owner.RPGMovement.Velocity);
            Owner.Animator.SetFloat("Forward", Mathf.Clamp(relativeVelocity.z, -1f, 1f), 0.08f, 0.08f);
            Owner.Animator.SetFloat("Turn", _turn, 0.08f, 0.08f);

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

            //calculate the ratio to ball point
            float ratio = Mathf.Clamp01(
                (_timeOfBallToPlayerOrthogonalPointOnBallPathCached - _timeOfBallToPlayerOrthogonalPointOnBallPath)
                / Mathf.Max(_timeOfBallToPlayerOrthogonalPointOnBallPathCached, 0.001f));

            //manipulate the player height
            float positionY = Mathf.Lerp(0f, RequiredJumpHeight, ratio);

            //now move the player character depending on the height
            Vector3 localPosition = new Vector3(Owner.ModelRoot.transform.localPosition.x, positionY, Owner.ModelRoot.transform.localPosition.z);
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
