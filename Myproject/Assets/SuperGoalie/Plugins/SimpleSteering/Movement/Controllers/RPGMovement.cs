using UnityEngine;

namespace Assets.SimpleSteering.Scripts.Movement
{
    [RequireComponent(typeof(CapsuleCollider))]
    [RequireComponent(typeof(Rigidbody))]
    public class RPGMovement : MonoBehaviour
    {
        [Header("Attributes")]
        public float Acceleration = 1f;
        public float Agility = 1f;
        public float RotationSpeed = 3f;
        public float Speed = 4f;

        public bool Steer;

        public bool Track;

        public float CurrentSpeed { get; set; }

        public CapsuleCollider CapsuleCollider { get; set; }

        public Rigidbody RigidBody { get; set; }

        public Vector3 MovementDirection { get; set; }

        public Vector3 RotationDirection { get; set; }

        private void Awake()
        {
            //intialize 
            CapsuleCollider = GetComponent<CapsuleCollider>();
            RigidBody = GetComponent<Rigidbody>();
            CurrentSpeed = 0f;
        }

        private void FixedUpdate()
        {
            //do the movement and rotation here
            if(Steer)
                Move(MovementDirection);

            if(Track)
                Rotate(RotationDirection);
        }

        /// <summary>
        /// Resets this instance
        /// </summary>
        public void Reset()
        {
            Velocity = Vector3.zero;
        }

        /// <summary>
        /// Disables steering
        /// </summary>
        public void SetSteeringOff()
        {
            Steer = false;
        }

        /// <summary>
        /// Enables steering
        /// </summary>
        public void SetSteeringOn()
        {
            Steer = true;
        }

        /// <summary>
        /// Disables tracking
        /// </summary>
        public void SetTrackingOff()
        {
            Track = false;
        }

        /// <summary>
        /// Enables tracking
        /// </summary>
        public void SetTrackingOn()
        {
            Track = true;
        }

        /// <summary>
        /// Initializes this instance with passed data
        /// </summary>
        /// <param name="acceleration">the acceleration of this instance</param>
        /// <param name="agility">the quickness in changing direction whilst moving.</param>
        /// <param name="rotationSpeed">the rotation speed of this instance</param>
        /// <param name="speed">the movement speed for this instance</param>
        public void Init(float acceleration, float agility, float rotationSpeed, float speed)
        {
            Acceleration = acceleration;
            RotationSpeed = rotationSpeed;
            Speed = speed;
        }

        /// <summary>
        /// Moves this instance in the specified direction
        /// </summary>
        /// <param name="direction">the direction to move to</param>
        public void SetMoveDirection(Vector3 direction)
        {
            //set the movement direction
            MovementDirection = direction;
        }

        /// <summary>
        /// Moves this instance to the target
        /// </summary>
        /// <param name="target">the steering target</param>
        public void SetMoveTarget(Vector3 target)
        {
            //find the direction to target
            Vector3 direction = target - transform.position;
            direction.y = 0f;

            //set the direction
            SetMoveDirection(direction);
        }

        /// <summary>
        /// Moves this instance
        /// </summary>
        /// <param name="direction">direction of movement</param>
        public void Move(Vector3 direction)
        {
            //accelerate
            CurrentSpeed = Mathf.MoveTowards(CurrentSpeed, Speed, Acceleration * Time.fixedDeltaTime);

            // Never overshoot the requested point. Overshoot used to make the keeper
            // oscillate around the interception point and visually miss a reachable ball.
            float distance = MovementDirection.magnitude;
            float step = Mathf.Min(distance, CurrentSpeed * Time.fixedDeltaTime);
            if (distance > Mathf.Epsilon)
                RigidBody.MovePosition(RigidBody.position + MovementDirection / distance * step);
        }

        /// <summary>
        /// Rotates this instance to face the specified direction
        /// </summary>
        /// <param name="direction"></param>
        public void SetRotateFaceDirection(Vector3 direction)
        {
            //set the rotation direction
            RotationDirection = direction;
        }

        /// <summary>
        /// Faces the position
        /// </summary>
        /// <param name="target"></param>
        public void SetRotateFacePosition(Vector3 target)
        {
            //find the rotation direction
            Vector3 direction = target - transform.position;
            direction.y = 0f;

            //set the rotation direction
            SetRotateFaceDirection(direction);
        }

        /// <summary>
        /// Rotates this instance
        /// </summary>
        /// <param name="direction">direction to rotate to</param>
        public void Rotate(Vector3 direction)
        {
            //rotate if we have direction
            if (direction.magnitude >= 0.01f)
            {
                //get the target rotation
                Quaternion targetRotation = Quaternion.LookRotation(direction);

                //rotate to target
                Quaternion currTargetRotation;
                currTargetRotation = Quaternion.Lerp(transform.rotation, targetRotation, RotationSpeed * Time.fixedDeltaTime);

                //rotate here
                RigidBody.MoveRotation(currTargetRotation);

            }
        }

        /// <summary>
        /// Property to access the velocity of the player
        /// </summary>
        public Vector3 Velocity
        {
            get
            {
                return RigidBody.velocity;
            }

            set
            {
                RigidBody.velocity = value;
            }
        }
    }
}
