using Assets.SuperGoalie.Scripts.Entities;
using Assets.SuperGoalie.Scripts.FSMs;
using Assets.SuperGoalie.Scripts.States.GoalKeeperStates.Idle.MainState;
using Assets.SuperGoalie.Scripts.States.GoalKeeperStates.InterceptShot.MainState;
using RobustFSM.Base;
using System;
using UnityEngine;

namespace Assets.SuperGoalie.Scripts.States.GoalKeeperStates.Dive.MainState
{
    public class PunchBallMainState : BState
    {
        float _time;
        float _turn;
        float _weightMultiplier;
        Vector3 _leftHandTargetPosition;
        Vector3 _rightHandTargetPosition;

        // ── 收尾阶段 ──────────────────────────────────────────────────────
        // 扑救动作全长 2.5–3.5 s，碰到球只占前 18%–30%，后面的倒地与起身才是
        // 「看着像扑救」的关键。这里按动作剩余时长走完，不再让球一到点就掐断。
        float _elapsed;
        float _remainAnimTime;
        float _finishSpeed = 1.6f;
        float _startY;

        public override void Enter()
        {
            base.Enter();

            _time = 0f;
            _elapsed = 0f;

            var intercept = Machine.GetState<InterceptShotMainState>();
            _leftHandTargetPosition = intercept.LeftHandTargetPosition;
            _rightHandTargetPosition = intercept.RightHandTargetPosition;
            _turn = intercept.Turn;

            var dive = intercept.DiveInfo;
            _remainAnimTime = Mathf.Max(0.30f, (1f - dive.ContactNorm) * dive.Length);
            _startY = Owner.ModelRoot.transform.localPosition.y;

            // 收尾提速：完整倒地起身约 2.5 s，提到 1.6 倍后约 1.6 s，节奏不至于拖沓
            Owner.Animator.ResetTrigger("Exit");
            Owner.Animator.speed = _finishSpeed;

            // 接触瞬间停止代码平移，位移完全由动作接管
            Owner.RPGMovement.SetSteeringOff();

            // Contact and deflection are owned by BallTrajectoryPlayer. This state
            // only transitions the animation after an actual swept contact.
            if (Owner.SaveAttemptSuccess)
            {
                    var gm = Managers.GameManager.Instance;
                    if (gm != null)
                        gm.ShowStatus(string.Format("{0}! (\u8d28\u91cf {1:0}%)", GetContactLabel(Owner.LastContactKind), Owner.SaveProbability * 100f));
                }

            Action temp = Owner.OnPunchBall;
            if (temp != null)
                temp.Invoke();

            if (Owner.Goal != null)
            {
                float lateral = Vector3.Dot(Owner.Position - Owner.Goal.CsvCoordinateOrigin, Owner.Goal.PitchRight);
                Debug.Log(string.Format("[GKContact] lateral={0:F2}m modelY={1:F2} remain={2:F2}s",
                    lateral, Owner.ModelRoot.localPosition.y, _remainAnimTime));
            }
        }

        public override void Execute()
        {
            base.Execute();

            // 等倒地与起身播完，再切回待机
            _elapsed += Time.deltaTime;
            if (_elapsed * _finishSpeed >= _remainAnimTime - 0.05f)
            {
                ExitDive();
                return;
            }

            // 兜底：若动画已经被别处切回 Idle，直接同步 FSM
            if (Owner.Animator.GetCurrentAnimatorStateInfo(0).IsName("Idle"))
                Machine.ChangeState<IdleMainState>();
        }

        void ExitDive()
        {
            Owner.Animator.speed = 1f;
            Owner.Animator.SetTrigger("Exit");
            Machine.ChangeState<IdleMainState>();
        }

        public override void OnAnimatorIK(int layerIndex)
        {
            base.OnAnimatorIK(layerIndex);

            //declare the weights
            float leftHandWeight = 0f;
            float rightHandWeight = 0f;
            float lookAtWeight = 0f;

            //set the time
            if(_time < 1f)
                _time += 10 * Time.deltaTime;

            //set the weight multiplier
            _weightMultiplier = Mathf.Lerp(1f, 0f, _time);

            //choose which hands to effect
            if (_turn == 0f)
            {
                //set the weights
                leftHandWeight = _weightMultiplier;
                rightHandWeight = _weightMultiplier;
                lookAtWeight = _weightMultiplier;

                //set the animations weights
                Owner.Animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, leftHandWeight);
                Owner.Animator.SetIKPositionWeight(AvatarIKGoal.RightHand, rightHandWeight);

                //set the animations positions
                Owner.Animator.SetIKPosition(AvatarIKGoal.LeftHand, _leftHandTargetPosition);
                Owner.Animator.SetIKPosition(AvatarIKGoal.RightHand, _rightHandTargetPosition);
            }
            else if (_turn == -1)
            {
                //set the weights
                leftHandWeight = _weightMultiplier;
                lookAtWeight = _weightMultiplier;

                //set the animations weights
                Owner.Animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, leftHandWeight);

                //set the animations positions
                Owner.Animator.SetIKPosition(AvatarIKGoal.LeftHand, _leftHandTargetPosition);

            }
            else if (_turn == 1)
            {
                //set the weights
                rightHandWeight = _weightMultiplier;
                lookAtWeight = _weightMultiplier;

                //set the animations weights
                Owner.Animator.SetIKPositionWeight(AvatarIKGoal.RightHand, rightHandWeight);

                //set the animations positions
                Owner.Animator.SetIKPosition(AvatarIKGoal.RightHand, _rightHandTargetPosition);
            }

            //set the look target
            Owner.Animator.SetLookAtWeight(lookAtWeight);
            Owner.Animator.SetLookAtPosition(Owner.Ball.Position);
        }

        public override void OnAnimatorMove()
        {
            base.OnAnimatorMove();

            // 倒地与起身的高度变化同样来自 root motion。
            // 之前这里把高度强行压向 0，但真正的问题是 root motion 被整体丢弃，
            // 人既不下降也不横移——现在交给动画自己给高度。
            Vector3 localPosition = Owner.ModelRoot.transform.localPosition;
            localPosition.y += Owner.Animator.deltaPosition.y;
            Owner.ModelRoot.transform.localPosition = localPosition;
        }

        float GetWorldBallRadius()
        {
            if (Owner.Ball == null || Owner.Ball.SphereCollider == null)
                return 0.11f;

            Vector3 scale = Owner.Ball.SphereCollider.transform.lossyScale;
            float largestScale = Mathf.Max(Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y)), Mathf.Abs(scale.z));
            return Owner.Ball.SphereCollider.radius * Mathf.Max(0.01f, largestScale);
        }

        bool HasBallFullyCrossedGoalLine(float ballRadius)
        {
            if (Owner.Ball == null || Owner.Goal == null)
                return false;

            float distanceIntoPitch = Vector3.Dot(
                Owner.Ball.CenterPosition - Owner.Goal.CsvCoordinateOrigin,
                Owner.Goal.PitchForward);
            return distanceIntoPitch < -ballRadius;
        }

        Vector3 GetPitchForward()
        {
            if (Owner.Goal != null)
                return Owner.Goal.PitchForward;

            Vector3 forward = Vector3.ProjectOnPlane(Owner.transform.forward, Vector3.up);
            return forward.sqrMagnitude > Mathf.Epsilon ? forward.normalized : Vector3.forward;
        }

        Vector3 ClampInFrontOfGoalLine(Vector3 position, float ballRadius)
        {
            if (Owner.Goal == null)
                return position;

            Vector3 pitchForward = Owner.Goal.PitchForward;
            float distanceIntoPitch = Vector3.Dot(position - Owner.Goal.CsvCoordinateOrigin, pitchForward);
            float minDistance = ballRadius + 0.25f;
            return distanceIntoPitch < minDistance
                ? position + pitchForward * (minDistance - distanceIntoPitch)
                : position;
        }
        string GetContactLabel(GoalKeeper.KeeperContactKind kind)
        {
            switch (kind)
            {
                case GoalKeeper.KeeperContactKind.Hand:
                    return "\u624b\u638c\u6251\u51fa";
                case GoalKeeper.KeeperContactKind.Arm:
                    return "\u624b\u81c2\u6321\u51fa";
                default:
                    return "\u8eab\u4f53\u5c01\u6321";
            }
        }

        GoalKeeper Owner
        {
            get
            {
                return ((GoalKeeperFSM)SuperMachine).Owner;
            }
        }
    }
}
