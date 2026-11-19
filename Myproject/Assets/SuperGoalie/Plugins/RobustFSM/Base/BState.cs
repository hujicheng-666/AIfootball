using RobustFSM.Interfaces;
using System;
using UnityEngine;

namespace RobustFSM.Base
{
    [Serializable]
    public abstract class BState : IState
    {
        public string StateName { get; internal set; }

        ///=================================================================================================
        /// <summary>   Gets or sets the machine. </summary>
        ///
        /// <value> The machine. </value>
        ///=================================================================================================

        public IFSM Machine { get; internal set; }

        ///=================================================================================================
        /// <summary>   Gets or sets the super machine. </summary>
        ///
        /// <value> The super machine. </value>
        ///=================================================================================================

        public IFSM SuperMachine { get; internal set; }

        public virtual void Initialize()
        {
            //if no name hase been specified set the name of this instance to the the
            if (String.IsNullOrEmpty(StateName))
                StateName = GetType().Name;
        }

        public virtual void Enter() { }
        public virtual void Execute() { }
        public virtual void ManualExecute() { }
        public virtual void PhysicsExecute() { }
        public virtual void PostExecute() { }
        public virtual void Exit() { }

        public virtual void OnCollisionEnter(Collision collision) { }
        public virtual void OnCollisionStay(Collision collision) { }
        public virtual void OnCollisionExit(Collision collision) { }
        public virtual void OnTriggerEnter(Collider collider) { }
        public virtual void OnTriggerStay(Collider collider) { }
        public virtual void OnTriggerExit(Collider collider) { }
        public virtual void OnAnimatorIK(int layerIndex) { }
        public virtual void OnAnimatorMove() { }

        public virtual string GetStateName()
        {
            return StateName;
        }

        public T GetMachine<T>() where T : IFSM
        {
            return (T)SuperMachine;
        }
    }
}