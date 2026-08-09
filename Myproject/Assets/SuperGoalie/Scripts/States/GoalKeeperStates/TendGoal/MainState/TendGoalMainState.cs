using Assets.SuperGoalie.Scripts.Entities;
using Assets.SuperGoalie.Scripts.FSMs;
using Assets.SuperGoalie.Scripts.States.GoalKeeperStates.Idle.MainState;
using Assets.SuperGoalie.Scripts.States.GoalKeeperStates.InterceptShot.MainState;
using RobustFSM.Base;
using UnityEngine;

namespace Assets.SuperGoalie.Scripts.States.GoalKeeperStates.TendGoal.MainState
{
    public class TendGoalMainState : BState
    {
        float _timeSinceLastUpdate;
        Vector3 _steeringTarget;
        Vector3 _prevBallPosition;

        public override void Enter()
        {
            base.Enter();

            //set some data
            _prevBallPosition = 1000 * Vector3.one;
            _timeSinceLastUpdate = 0f;

            //set the rpg movement
            Owner.RPGMovement.SetSteeringOn();
            Owner.RPGMovement.SetTrackingOn();
            Owner.RPGMovement.Speed = Owner.TendGoalSpeed;

            //register to some events
            Owner.OnBallLaunched += Instance_OnBallLaunched;

            //set the animator
            Owner.Animator.SetTrigger("TendGoal");
        }

        public override void Execute()
        {
            base.Execute();

            //if the ball is not within threatening distance then idle
            if (!Owner.IsBallWithThreateningDistance())
                Machine.ChangeState<IdleMainState>();

            //get the entity positions
            Vector3 ballPosition = new Vector3(Owner.Ball.Position.x, 0f, Owner.Ball.Position.z);

            //set the look target
            Owner.RPGMovement.SetRotateFacePosition(ballPosition);

            //if I have exhausted my time then update the tend point
            if (_timeSinceLastUpdate <= 0f)
            {
                //do not continue if the ball didnt move
                if (_prevBallPosition != ballPosition)
                {
                    //cache the ball position
                    _prevBallPosition = ballPosition;

                    // Cut the shooting angle on the ray from goal centre to ball.
                    // This mirrors the separation used by modern football games:
                    // positioning chooses the pose; animation/physics executes it.
                    Vector3 origin = Owner.Goal.CsvCoordinateOrigin;
                    Vector3 pitchForward = Owner.Goal.PitchForward;
                    Vector3 pitchRight = Owner.Goal.PitchRight;
                    Vector3 goalSpaceBall = Owner.Goal.WorldToGoalCoordinates(ballPosition);
                    float targetDepth = Mathf.Clamp(Owner.TendGoalDistance, 0.45f, 3f);
                    float depth = Mathf.Max(targetDepth, goalSpaceBall.z);
                    float lateral = goalSpaceBall.x * targetDepth / depth;
                    float halfWidth = Mathf.Max(0.5f, Owner.Goal.GoalWidth * 0.5f - 0.28f);
                    lateral = Mathf.Clamp(lateral, -halfWidth, halfWidth);

                    float error = (1f - Mathf.Clamp01(Owner.GoalKeeping)) * 0.35f;
                    lateral += Random.Range(-error, error);
                    float depthError = Random.Range(-error * 0.35f, error * 0.35f);
                    _steeringTarget = origin
                        + pitchRight * Mathf.Clamp(lateral, -halfWidth, halfWidth)
                        + pitchForward * Mathf.Max(0.35f, targetDepth + depthError);
                    _steeringTarget.y = Owner.Position.y;
                }

                //reset the time 
                _timeSinceLastUpdate = 2f * (1f - Owner.GoalKeeping);
                if (_timeSinceLastUpdate == 0f)
                    _timeSinceLastUpdate = 2f * 0.1f;
            }

            //decrement the time
            _timeSinceLastUpdate -= Time.deltaTime;
           
            //set the ability to steer here
            Owner.RPGMovement.Steer = Vector3.Distance(Owner.Position, _steeringTarget) >= 0.08f;
            Owner.RPGMovement.SetMoveTarget(_steeringTarget);

            //get my relative velocity
            Vector3 relativeVelocity = Owner.transform.InverseTransformDirection(Owner.RPGMovement.Velocity);
            float clampedForward = Mathf.Clamp(relativeVelocity.z, -1f, 0.5f);
            float clampedSide = Mathf.Clamp(relativeVelocity.x, -1f, 1f);

            //update the animator
            Owner.Animator.SetFloat("Forward", clampedForward, 0.1f, 0.1f);
            Owner.Animator.SetFloat("Turn", clampedSide, 0.1f, 0.1f);
        }


        public override void Exit()
        {
            base.Exit();

            //deregister to some events
            Owner.OnBallLaunched -= Instance_OnBallLaunched;

            //set the animator
            Owner.RPGMovement.SetTrackingOff();
            Owner.Animator.ResetTrigger("TendGoal");
        }

        private void Instance_OnBallLaunched(float flightTime, float velocity, Vector3 initial, Vector3 target)
        {
            //set some variables
            Owner.BallInitialPosition = initial;
            Owner.BallHitTarget = target;
            Owner.BallFlightTime = flightTime;
            Owner.BallVelocity = velocity;

            // 无论射正射偏，全部尝试拦截扑救
            Machine.ChangeState<InterceptShotMainState>();
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
