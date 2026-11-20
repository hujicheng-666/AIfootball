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

        public override void Enter()
        {
            base.Enter();

            _time = 0f;

            _leftHandTargetPosition = Machine.GetState<InterceptShotMainState>().LeftHandTargetPosition;
            _rightHandTargetPosition = Machine.GetState<InterceptShotMainState>().RightHandTargetPosition;
            _turn = Machine.GetState<InterceptShotMainState>().Turn;

            // Contact and deflection are owned by BallTrajectoryPlayer. This state
            // only transitions the animation after an actual swept contact.
            if (Owner.SaveAttemptSuccess)
            {
                    var gm = Managers.GameManager.Instance;
                    if (gm != null)
                        gm.ShowStatus(string.Format("{0}! (\u8d28\u91cf {1:0}%)", GetContactLabel(Owner.LastContactKind), Owner.SaveProbability * 100f));
                }

            Owner.Animator.SetTrigger("Exit");
            Action temp = Owner.OnPunchBall;
            if (temp != null)
                temp.Invoke();
        }

        public override void Execute()
        {
            base.Execute();

            //go to idle state the moment the player gets into idle state
            if (Owner.Animator.GetCurrentAnimatorStateInfo(0).IsName("Idle"))
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

            //manipulate the player height
            Owner.ModelRoot.transform.localPosition = Vector3.zero;
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
