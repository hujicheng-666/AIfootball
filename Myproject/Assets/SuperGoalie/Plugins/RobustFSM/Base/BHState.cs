using System;
using UnityEngine;
using System.Collections.Generic;
using RobustFSM.Interfaces;

namespace RobustFSM.Base
{
    [Serializable]
    public abstract class BHState : BState, IFSM
    {
        /// <summary>
        /// A reference to the update frequency for this state machine
        /// </summary>
        public float UpdateFrequency { get; set; }

        /// <summary>
        /// A refernce to the name of this instance
        /// </summary>
        public string MachineName { get; set; }

        /// <summary>
        /// A reference to the current state of this FSM
        /// </summary>
        protected BState CurrentState { get; set; }

        /// <summary>
        /// A reference to the initial state of this FSM
        /// </summary>
        protected BState InitialState { get; set; }

        /// <summary>
        /// A reference to the next state of this FSM
        /// </summary>
        protected BState NextState { get; set; }

        /// <summary>
        /// A reference to the previous state of this FSM
        /// </summary>
        protected BState PreviousState { get; set; }

        /// <summary>
        /// A reference to the states of this instance
        /// </summary>
        private Dictionary<Type, BState> _states = new Dictionary<Type, BState>();

        #region FSM Initialization Methods
        /// <summary>
        /// REQUIRES IMPL
        /// Adds states to the machine with calls to AddState<>()
        ///     
        /// When all states have been added set the initial state with 
        /// a call toSetInitialState<>()
        /// </summary>
        public abstract void AddStates();

        /// <summary>
        /// Add the state to the FSM
        /// </summary>
        /// <typeparam name="T">state type</typeparam>
        public void AddState<T>() where T : BState, new()
        {
            if (!ContainsState<T>())
            {
                BState item = new T();
                item.Machine = this;
                item.SuperMachine = SuperMachine;

                States.Add(typeof(T), item);
            }
        }

        /// <summary>
        /// Initializes this state machine
        /// </summary>
        public override void Initialize()
        {
            base.Initialize();

            //if no name hase been specified set the name of this instance to the the
            if (String.IsNullOrEmpty(MachineName))
                MachineName = GetType().Name;

            //add the states
            AddStates();

            //set the current state to be the initial state
            CurrentState = InitialState;

            //throw an error if current is null
            if (CurrentState == null)
                throw new Exception("\n" + MachineName + ".nextState is null on Initialize()!\tDid you forget to call SetInitialState()?\n");

            //initialize every state
            foreach (KeyValuePair<Type, BState> pair in States)
                pair.Value.Initialize();
        }

        /// <summary>
        /// Sets the initial state for this FSM
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public void SetInitialState<T>() where T : BState
        {
            InitialState = States[typeof(T)];
        }
        #endregion

        #region BState Overrides

        /// <summary>
        /// Raises the enter state event
        /// </summary>
        public override void Enter()
        {
            base.Enter();

            //make sure the current state is the
            //initial state
            CurrentState = InitialState;

            if (CurrentState != null)
                CurrentState.Enter();
        }

        /// <summary>
        /// Raises the exit state event
        /// </summary>
        public override void Exit()
        {
            base.Exit();

            //run the exit for the current state
            if (CurrentState != null)
                CurrentState.Exit();
        }

        /// <summary>
        /// Raises the custom execute state event
        /// </summary>
        public override void ManualExecute()
        {
            base.ManualExecute();

            if (CurrentState != null)
                CurrentState.ManualExecute();
        }

        /// <summary>
        /// Raises the execute state event
        /// </summary>
        public override void Execute()
        {
            base.Execute();

            if (CurrentState != null)
                CurrentState.Execute();
        }

        /// <summary>
        /// Raises the physics execute state event
        /// </summary>
        public override void PhysicsExecute()
        {
            base.PhysicsExecute();

            if (CurrentState != null)
                CurrentState.PhysicsExecute();
        }

        /// <summary>
        /// Raises the post execute state event
        /// </summary>
        public override void PostExecute()
        {
            base.PostExecute();

            if (CurrentState != null)
                CurrentState.PostExecute();
        }

        /// <summary>
        /// Raise the oncollision enter event
        /// </summary>
        /// <param name="collision"></param>
        public override void OnCollisionEnter(Collision collision)
        {
            if (CurrentState != null)
                CurrentState.OnCollisionEnter(collision);
        }

        /// <summary>
        /// Raise the oncollision stay event
        /// </summary>
        /// <param name="collision"></param>
        public override void OnCollisionStay(Collision collision)
        {
            if (CurrentState != null)
                CurrentState.OnCollisionStay(collision);
        }

        /// <summary>
        /// Raise the oncollision exit event
        /// </summary>
        /// <param name="collision"></param>
        public override void OnCollisionExit(Collision collision)
        {
            if (CurrentState != null)
                CurrentState.OnCollisionExit(collision);
        }

        /// <summary>
        /// Raise the ontrigger enter event
        /// </summary>
        /// <param name="collision"></param>
        public override void OnTriggerEnter(Collider collider)
        {
            if (CurrentState != null)
                CurrentState.OnTriggerEnter(collider);
        }

        /// <summary>
        /// Raise the ontrigger stay event
        /// </summary>
        /// <param name="collision"></param>
        public override void OnTriggerStay(Collider collider)
        {
            if (CurrentState != null)
                CurrentState.OnTriggerStay(collider);
        }

        /// <summary>
        /// Raise the ontrigger exit event
        /// </summary>
        /// <param name="collision"></param>
        public override void OnTriggerExit(Collider collider)
        {
            if (CurrentState != null)
                CurrentState.OnTriggerExit(collider);
        }

        /// <summary>
        /// Raise the onanimator ik event
        /// </summary>
        /// <param name="collision"></param>
        public override void OnAnimatorIK(int layerIndex)
        {
            if (CurrentState != null)
                CurrentState.OnAnimatorIK(layerIndex);
        }
        #endregion

        #region FSM Methods
        /// <summary>
        /// Triggers a state transition of the FSM to the specified state
        /// </summary>
        /// <typeparam name="T">the next state</typeparam>
        public void ChangeState<T>() where T : BState
        {
            ChangeState(typeof(T));
        }

        /// <summary>
        /// Triggers a state transition of the FSM to the specified state
        /// </summary>
        /// <param name="t"></param>
        private void ChangeState(Type t)
        {
            try
            {
                //cache the correct states
                PreviousState = CurrentState;
                NextState = States[t];

                //exit the current state
                CurrentState.Exit();
                CurrentState = NextState;
                NextState = null;

                //enter the current state
                CurrentState.Enter();
            }
            catch (KeyNotFoundException e)
            {
                throw new Exception("\n" + MachineName + " could not be found in the state machine" + e.Message);
            }
        }

        /// <summary>
        /// Checks whether this FSM contains the passed state
        /// </summary>
        /// <typeparam name="T">the state type</typeparam>
        /// <returns><c>true</c>, if state is such type is available else <c>false</c></returns>
        public bool ContainsState<T>() where T : BState
        {
            return States.ContainsKey(typeof(T));
        }

        /// <summary>
        /// Checks whether this FSM contains the passed state type
        /// </summary>
        /// <typeparam name="T">the state type</typeparam>
        /// <returns><c>true</c>, if state is such type is available else <c>false</c></returns>
        public bool ContainsState(Type T)
        {
            return States.ContainsKey(T);
        }

        /// <summary>
        /// Checks whether the current state in the FSM matches the passed state
        /// </summary>
        /// <typeparam name="T">the state type</typeparam>
        /// <returns><c>true</c>, if the passed state matches the current state<c>false</c></returns>
        public bool IsCurrentState<T>() where T : BState
        {
            return (CurrentState.GetType() == typeof(T)) ? true : false;
        }

        /// <summary>
        /// Checks whether the previous state in the FSM matches the passed state
        /// </summary>
        /// <typeparam name="T">the state type</typeparam>
        /// <returns><c>true</c>, if the passed state matches the previous state<c>false</c></returns>
        public bool IsPreviousState<T>() where T : BState
        {
            return (PreviousState.GetType() == typeof(T)) ? true : false;
        }

        /// <summary>
        /// Returns the current state
        /// </summary>
        /// <typeparam name="T">the state type</typeparam>
        /// <returns>the current state</returns>
        public T GetCurrentState<T>() where T : BState
        {
            return (T)CurrentState;
        }

        /// <summary>
        /// Returns the previous state
        /// </summary>
        /// <typeparam name="T">the state type</typeparam>
        /// <returns>the previous state</returns>
        public T GetPreviousState<T>() where T : BState
        {
            return (T)PreviousState;
        }

        /// <summary>
        /// Retrieves the specified state from the FSM
        /// </summary>
        /// <typeparam name="T">the state type</typeparam>
        /// <returns>the previous state</returns>
        public T GetState<T>() where T : BState
        {
            return (T)States[typeof(T)];
        }

        /// <summary>
        /// Triggers the FSM to go to the previous state
        /// </summary>
        public void GoToPreviousState()
        {
            ChangeState(PreviousState.GetType());
        }

        /// <summary>
        /// Removes the passed state from the state machine
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public void RemoveState<T>() where T : BState
        {
            Type t = typeof(T);
            if (States.ContainsKey(t))
                States.Remove(t);
        }

        /// <summary>
        /// Removes the passed state from the state machine
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public void RemoveState(Type T)
        {
            if (States.ContainsKey(T))
                States.Remove(T);
        }

        /// <summary>
        /// Removes all states in the FSM
        /// </summary>
        public void RemoveAllStates()
        {
            States.Clear();
        }
        #endregion

        public override string GetStateName()
        {
            //prepare the state name
            string returnValue = MachineName + "\n" + CurrentState.GetStateName(); 

            //return the name
            return returnValue;
        }

        #region Properties

        protected Dictionary<Type, BState> States
        {
            get
            {
                return _states;
            }

            set
            {
                _states = value;
            }
        }
        #endregion

    }
}