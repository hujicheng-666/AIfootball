using Assets.SuperGoalie.Scripts.Entities;
using Assets.SuperGoalie.Scripts.States.GoalKeeperStates.Dive.MainState;
using Assets.SuperGoalie.Scripts.States.GoalKeeperStates.Idle.MainState;
using Assets.SuperGoalie.Scripts.States.GoalKeeperStates.InterceptShot.MainState;
using Assets.SuperGoalie.Scripts.States.GoalKeeperStates.TendGoal.MainState;
using RobustFSM.Base;

namespace Assets.SuperGoalie.Scripts.FSMs
{
    public class GoalKeeperFSM : BFSM
    {
        public GoalKeeper Owner { get; set; }

        public override void AddStates()
        {
            AddState<PunchBallMainState>();
            AddState<IdleMainState>();
            AddState<InterceptShotMainState>();
            AddState<TendGoalMainState>();

            SetInitialState<IdleMainState>();
        }

        private void Awake()
        {
            Owner = GetComponent<GoalKeeper>();
        }
    }
}
