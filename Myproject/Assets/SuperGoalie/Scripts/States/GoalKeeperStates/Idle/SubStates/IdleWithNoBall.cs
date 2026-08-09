using System;
using Assets.SuperGoalie.Scripts.Entities;
using Assets.SuperGoalie.Scripts.FSMs;
using RobustFSM.Base;

namespace Assets.SuperGoalie.Scripts.States.GoalKeeperStates.Idle.SubStates
{
    public class IdleWithNoBall : BState
    {
        public override void Enter()
        {
            base.Enter();

            //register to goalkeeper events
            Owner.OnHasBall += Instance_OnHasBall;

            //set the animator
            Owner.Animator.SetBool("HasBall", false);
        }

        public override void Exit()
        {
            base.Exit();

            //deregister to goalkeeper events
            Owner.OnHasBall -= Instance_OnHasBall;
        }

        private void Instance_OnHasBall()
        {
            Machine.ChangeState<IdleWithBall>();
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
