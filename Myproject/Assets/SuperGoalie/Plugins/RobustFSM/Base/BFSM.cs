using System;
using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using RobustFSM.Interfaces;
using UnityEditor;

namespace RobustFSM.Base
{
    public abstract class BFSM : MonoBehaviour, IFSM
    {
        /// <summary>
        /// A reference to the update frequency for this state machine
        /// </summary>
        public float UpdateFrequency { get; set; }

        /// <summary>
        /// A reference to machine name of this instance
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
        /// A reference to the manual update coroutine
        /// </summary>
        private Coroutine _manualUpdateCoroutine;

        /// <summary>
        /// A reference to the states dictionary of this instance
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
                item.SuperMachine = this;

                States.Add(typeof(T), item);
            }
        }

        /// <summary>
        /// Initializes the FSM
        /// </summary>
        public virtual void Initialize()
        {
            //if no name hase been specified set the name of this instance to the the
            if (String.IsNullOrEmpty(MachineName))
                MachineName = GetType().Name;

            //add the states
            AddStates();

            //set the current state to be the initial state
            CurrentState = InitialState;

            //throw an error if we do not have an initial state
            if (CurrentState == null)
                throw new Exception("\n" + name + "Initial state not specified.\tSpecify the initial state inside the AddStates()!!!\n");

            //initialize every state
            foreach (KeyValuePair<Type, BState> pair in States)
            {
                //set the super machine and initialize the state
                pair.Value.Machine = this;
                pair.Value.SuperMachine = this;
                pair.Value.Initialize();
            }

            //change to the current state
            CurrentState.Enter();

            //start the custom update coroutine if valid
            if (UpdateFrequency > 0)
                CustomUpdateCoroutine = StartCoroutine(ManualUpdate());
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

        #region MonoBehaviour Methods
        /// <summary>
        /// Raises the start event
        /// </summary>
        public virtual void Start()
        {
            Initialize();
        }

        /// <summary>
        /// Raise the manual update event
        /// </summary>
        /// <returns></returns>
        public virtual IEnumerator ManualUpdate()
        {
            //continously run the current state custom execute
            while (true)
            {
                //if we have a current state defined run custom execute
                if (CurrentState != null)
                    CurrentState.ManualExecute();

                //wait for the specified update frequency
                yield return new WaitForSeconds(UpdateFrequency);
            }
        }

        /// <summary>
        /// Raises the Update event
        /// </summary>
        public virtual void Update()
        {
            //if we have a current state defined run execute
            if (CurrentState != null)
                CurrentState.Execute();
        }

        /// <summary>
        /// Raises the Fixed Update event
        /// </summary>
        public virtual void FixedUpdate()
        {
            //if we have a current state defined run physics execute
            if (CurrentState != null)
                CurrentState.PhysicsExecute();
        }

        /// <summary>
        /// Raises the Late Update event
        /// </summary>
        public virtual void LateUpdate()
        {
            //if we have a current state defined run late execute
            if (CurrentState != null)
                CurrentState.PostExecute();
        }

        /// <summary>
        /// Raises the OnCollision Enter event
        /// </summary>
        public virtual void OnCollisionEnter(Collision collision)
        {
            //if we have a current state defined run oncollision enter
            if (CurrentState != null)
                CurrentState.OnCollisionEnter(collision);
        }

        /// <summary>
        /// Raises the OnCollision Stay event
        /// </summary>
        public virtual void OnCollisionStay(Collision collision)
        {
            //if we have a current state defined run oncollision stay
            if (CurrentState != null)
                CurrentState.OnCollisionStay(collision);
        }

        /// <summary>
        /// Raises the OnCollision Exit event
        /// </summary>
        public virtual void OnCollisionExit(Collision collision)
        {
            //if we have a current state defined run oncollision exit
            if (CurrentState != null)
                CurrentState.OnCollisionExit(collision);
        }

        /// <summary>
        /// Raises the OnTrigger Enter event
        /// </summary>
        public virtual void OnTriggerEnter(Collider collider)
        {
            //if we have a current state defined run ontrigger enter
            if (CurrentState != null)
                CurrentState.OnTriggerEnter(collider);
        }

        /// <summary>
        /// Raises the OnTrigger Stay event
        /// </summary>
        public virtual void OnTriggerStay(Collider collider)
        {
            //if we have a current state defined run ontrigger stay
            if (CurrentState != null)
                CurrentState.OnTriggerStay(collider);
        }

        /// <summary>
        /// Raises the OnTrigger Exit event
        /// </summary>
        public virtual void OnTriggerExit(Collider collider)
        {
            //if we have a current state defined run ontrigger exit
            if (CurrentState != null)
                CurrentState.OnTriggerExit(collider);
        }

        /// <summary>
        /// Raises the OnAnimator IK event
        /// </summary>
        public void OnAnimatorIK(int layerIndex)
        {
            //if we have a current state defined run onanimator stay
            if (CurrentState != null)
                CurrentState.OnAnimatorIK(layerIndex);
        }

        public void OnAnimatorMove()
        {
            //if we have a current state defined run onanimator stay
            if (CurrentState != null)
                CurrentState.OnAnimatorMove();
        }

        #endregion

        void OnDrawGizmos()
        {
            if (CurrentState != null)
            {
                //set the print text
                string printText = MachineName + "\n" + CurrentState.GetStateName();

                //render the label
                //Handles.Label(transform.position, printText);
            }
        }

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
                throw new Exception("\n" + name + " could not be found in the state machine" + e.Message);
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
        /// Sets the custome update frequency of the FSM
        /// </summary>
        /// <param name="value"></param>
        public void SetUpdateFrequency(float value)
        {
            UpdateFrequency = value;
        }

        /// <summary>
        /// Starts the custom update loop
        /// </summary>
        public void StartCustomUpdate()
        {
            if (UpdateFrequency > 0)
                CustomUpdateCoroutine = StartCoroutine(ManualUpdate());
        }

        /// <summary>
        /// Stops the custom update loop
        /// </summary>
        public void StopCutomUpdate()
        {
            if (CustomUpdateCoroutine != null)
                StopCoroutine(CustomUpdateCoroutine);
        }

        /// <summary>
        /// Removes the passed state from the state machine
        /// </summary>
        /// <typeparam name="T"></typeparam>
        public void RemoveState<T>() where T : BState
        {
            Type t = typeof(T);
            if(States.ContainsKey(t))
                States.Remove(t);
        }

        /// <summary>
        /// Removes all states in the FSM
        /// </summary>
        public void RemoveAllStates()
        {
            States.Clear();
        }
        #endregion

        #region Properties
        protected Coroutine CustomUpdateCoroutine
        {
            get
            {
                return _manualUpdateCoroutine;
            }

            set
            {
                _manualUpdateCoroutine = value;
            }
        }

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