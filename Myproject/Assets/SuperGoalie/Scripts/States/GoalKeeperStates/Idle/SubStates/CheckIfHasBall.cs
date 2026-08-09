using Assets.SuperGoalie.Scripts.Entities;
using Assets.SuperGoalie.Scripts.FSMs;
using RobustFSM.Base;

namespace Assets.SuperGoalie.Scripts.States.GoalKeeperStates.Idle.SubStates
{
    public class CheckIfHasBall : BState
    {
        public override void Enter()
        {
            base.Enter();

            //go to appropriate state 
            if (Owner.HasBall)
                Machine.ChangeState<IdleWithBall>();
            else
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
