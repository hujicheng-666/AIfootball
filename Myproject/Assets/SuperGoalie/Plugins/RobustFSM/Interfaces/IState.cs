using UnityEngine;

namespace RobustFSM.Interfaces
{
    public interface IState
    {
        string StateName { get; }

         IFSM Machine { get; }

        IFSM SuperMachine { get; }

        string GetStateName();

        void Initialize();

        /// <summary>
        /// Called once when a state is activated
        /// </summary>
        void Enter();
        /// <summary>
        /// Called every frame execute
        /// </summary>
        void Execute();

        /// <summary>
        /// Called every custom execute
        /// </summary>
        void ManualExecute();

        /// <summary>
        /// Called every physics update
        /// </summary>
        void PhysicsExecute();

        /// <summary>
        /// Called every late execute
        /// </summary>
        void PostExecute();

        /// <summary>
        /// Called once when a state is deactivated
        /// </summary>
        void Exit();

        void OnCollisionEnter(Collision collision);
        void OnCollisionStay(Collision collision);
        void OnCollisionExit(Collision collision);

        void OnTriggerEnter(Collider collider);
        void OnTriggerStay(Collider collider);
        void OnTriggerExit(Collider collider);

        void OnAnimatorIK(int layerIndex);

        void OnAnimatorMove();

        T GetMachine<T>() where T : IFSM;
    }
}