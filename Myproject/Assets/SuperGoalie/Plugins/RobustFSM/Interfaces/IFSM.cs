using RobustFSM.Base;
using UnityEngine;

namespace RobustFSM.Interfaces
{
    /// <summary>
    /// Interface for the finite state machine
    /// </summary>
    public interface IFSM
    {
        string MachineName { get; set; }

        bool ContainsState<T>() where T : BState;

        bool IsCurrentState<T>() where T : BState;

        bool IsPreviousState<T>() where T : BState;

        T GetCurrentState<T>() where T : BState;

        T GetPreviousState<T>() where T : BState;

        T GetState<T>() where T : BState;

        void AddState<T>() where T : BState, new();

        void ChangeState<T>() where T : BState;

        void RemoveState<T>() where T : BState;

        void SetInitialState<T>() where T : BState;

        void GoToPreviousState();

        void OnCollisionEnter(Collision collision);

        void OnCollisionStay(Collision collision);

        void OnCollisionExit(Collision collision);

        void OnTriggerEnter(Collider collider);

        void OnTriggerStay(Collider collider);

        void OnTriggerExit(Collider collider);

        void RemoveAllStates();
    }
}