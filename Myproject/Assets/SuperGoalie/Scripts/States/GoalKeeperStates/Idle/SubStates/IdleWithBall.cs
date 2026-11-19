using Assets.SuperGoalie.Scripts.Entities;
using Assets.SuperGoalie.Scripts.FSMs;
using RobustFSM.Base;

namespace Assets.SuperGoalie.Scripts.States.GoalKeeperStates.Idle.SubStates
{
    public class IdleWithBall : BState
    {
        public override void Enter()
        {
            base.Enter();

            //register to goalkeeper events
            Owner.OnHasNoBall += Instance_OnHasNoBall;

            //set the various components
            Owner.Animator.SetBool("HasBall", true);
        }

        public override void Exit()
        {
            base.Exit();

            //deregister to goalkeeper events
            Owner.OnHasNoBall -= Instance_OnHasNoBall;
        }

        private void Instance_OnHasNoBall()
        {
            Machine.ChangeState<IdleWithNoBall>();
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
