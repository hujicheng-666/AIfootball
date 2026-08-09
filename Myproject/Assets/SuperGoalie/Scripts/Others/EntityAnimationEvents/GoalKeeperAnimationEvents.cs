using Assets.SuperGoalie.Scripts.Entities;
using Assets.SuperGoalie.Scripts.States.GoalKeeperStates.Idle.MainState;
using UnityEngine;

namespace Assets.SuperGoalie.Scripts.Others.EntitiesAnimationEvents
{
    public class GoalKeeperAnimationEvents : MonoBehaviour
    {
        public GoalKeeper Owner;

        public void GoToIdleState()
        {
            Owner.FSM.ChangeState<IdleMainState>();
        }

        public void GoToSleepState()
        {
            // 动画事件可能触发该方法；原先抛 NotImplementedException 会直接导致运行崩溃。
            // FSM 未定义独立的睡眠状态，因此安全地回到空闲状态。
            if (Owner != null && Owner.FSM != null)
                Owner.FSM.ChangeState<IdleMainState>();
        }

        public void OnAnimatorIK(int layerIndex)
        {
            Owner.FSM.OnAnimatorIK(layerIndex);
        }

        private void OnAnimatorMove()
        {
            Owner.FSM.OnAnimatorMove();
        }
    }
}
