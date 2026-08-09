using System;
using Assets.SuperGoalie.Scripts.Triggers;
using UnityEngine;

namespace Assets.SuperGoalie.Scripts.Entities
{
    public class Goal : MonoBehaviour
    {
        public const float PenaltySpotDistance = 11f;

        [SerializeField]
        GoalMouth _goalMouth;

        [SerializeField]
        GoalTrigger _goalTrigger;

        public bool HasCompleteGoalMouth
        {
            get
            {
                return _goalMouth._pointBottomLeft != null
                    && _goalMouth._pointBottomRight != null
                    && _goalMouth._pointTopLeft != null
                    && _goalMouth._pointTopRight != null;
            }
        }

        public void EnsureSceneReferences()
        {
            if (_goalTrigger == null)
                _goalTrigger = GetComponentInChildren<GoalTrigger>(true);

            // 自动给球门所有子物体加碰撞体，防止球穿模
            EnsureGoalColliders();
        }

        /// <summary>
        /// 自动加碰撞体：门柱用 CapsuleCollider，球门背后加 BoxCollider 兜底防穿网
        /// </summary>
        void EnsureGoalColliders()
        {
            if (!HasCompleteGoalMouth)
                return;

            Transform root = transform.Find("PenaltyPhysicsColliders");
            if (root != null && root.childCount > 0)
                return;
            if (root == null)
            {
                GameObject rootObject = new GameObject("PenaltyPhysicsColliders");
                rootObject.transform.SetParent(transform, false);
                root = rootObject.transform;
            }

            PhysicMaterial material = CreateGoalPhysicsMaterial();
            Vector3 origin = CsvCoordinateOrigin;
            Vector3 up = Vector3.up;
            Vector3 pitchForward = Vector3.ProjectOnPlane(transform.forward, up).normalized;
            Vector3 goalRight = Vector3.ProjectOnPlane(transform.right, up).normalized;
            Quaternion goalRotation = Quaternion.LookRotation(pitchForward, up);
            float width = Vector3.Distance(_goalMouth._pointBottomLeft.position, _goalMouth._pointBottomRight.position);
            float height = Vector3.Distance(_goalMouth._pointBottomLeft.position, _goalMouth._pointTopLeft.position);
            float depth = 1.15f;
            float postThickness = 0.12f;

            Vector3 leftPost = _goalMouth._pointBottomLeft.position;
            Vector3 rightPost = _goalMouth._pointBottomRight.position;
            // 门柱与横梁/网兜一样使用 goalRotation，避免球门旋转时碰撞体与门柱错位（导致球穿柱）
            CreateBox(root, "PenaltyPhysics_LeftPost", leftPost + up * (height * 0.5f), goalRotation, new Vector3(postThickness, height + postThickness, postThickness), material);
            CreateBox(root, "PenaltyPhysics_RightPost", rightPost + up * (height * 0.5f), goalRotation, new Vector3(postThickness, height + postThickness, postThickness), material);
            CreateBox(root, "PenaltyPhysics_Crossbar", origin + up * height, goalRotation, new Vector3(width + postThickness, postThickness, postThickness), material);

            CreateBox(root, "PenaltyPhysics_BackNet", origin - pitchForward * depth + up * (height * 0.5f), goalRotation, new Vector3(width + 0.5f, height + 0.35f, 0.10f), material);
            CreateBox(root, "PenaltyPhysics_LeftSideNet", leftPost - pitchForward * (depth * 0.5f) + up * (height * 0.5f), goalRotation, new Vector3(0.10f, height + 0.25f, depth), material);
            CreateBox(root, "PenaltyPhysics_RightSideNet", rightPost - pitchForward * (depth * 0.5f) + up * (height * 0.5f), goalRotation, new Vector3(0.10f, height + 0.25f, depth), material);
            CreateBox(root, "PenaltyPhysics_TopNet", origin - pitchForward * (depth * 0.5f) + up * (height + 0.05f), goalRotation, new Vector3(width + 0.4f, 0.10f, depth), material);
        }

        static PhysicMaterial CreateGoalPhysicsMaterial()
        {
            PhysicMaterial material = new PhysicMaterial("GoalAndNetPhysicMaterial");
            material.dynamicFriction = 0.55f;
            material.staticFriction = 0.55f;
            material.bounciness = 0.18f;
            material.frictionCombine = PhysicMaterialCombine.Maximum;
            material.bounceCombine = PhysicMaterialCombine.Average;
            return material;
        }

        static void CreateBox(Transform parent, string name, Vector3 worldPosition, Quaternion worldRotation, Vector3 size, PhysicMaterial material)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            obj.transform.position = worldPosition;
            obj.transform.rotation = worldRotation;
            BoxCollider collider = obj.AddComponent<BoxCollider>();
            collider.size = size;
            collider.sharedMaterial = material;
            collider.isTrigger = false;
        }

        internal bool IsPositionWithinGoalMouthFrustrum(Vector3 position)
        {
            //find the relative position to goal
            Vector3 relativePosition = transform.InverseTransformPoint(position);

            //find the relative position of each goal mouth
            Vector3 pointBottomLeftRelativePosition = transform.InverseTransformPoint(_goalMouth._pointBottomLeft.position);
            Vector3 pointBottomRightRelativePosition = transform.InverseTransformPoint(_goalMouth._pointBottomRight.position);
            Vector3 pointTopLeftRelativePosition = transform.InverseTransformPoint(_goalMouth._pointTopLeft.position);

            //check if the x- coordinate of the relative position lies within the goal mouth
            bool isPositionWithTheXCoordinates = relativePosition.x > pointBottomLeftRelativePosition.x && relativePosition.x < pointBottomRightRelativePosition.x;
            bool isPositionWithTheYCoordinates = relativePosition.y > pointBottomLeftRelativePosition.y && relativePosition.y < pointTopLeftRelativePosition.y;

            //the result is the combination of the two tests
            return isPositionWithTheXCoordinates && isPositionWithTheYCoordinates;
        }

        /// <summary>
        /// CSV coordinate origin: the point on the ground halfway between the two goal posts.
        /// </summary>
        public Vector3 CsvCoordinateOrigin
        {
            get
            {
                // 与 GoalWidth/GoalHeight 一致，球门口点未完整赋值时回退到球门自身位置，避免 NRE
                return HasCompleteGoalMouth
                    ? (_goalMouth._pointBottomLeft.position + _goalMouth._pointBottomRight.position) * 0.5f
                    : transform.position;
            }
        }


        public float GoalWidth
        {
            get
            {
                return HasCompleteGoalMouth
                    ? Vector3.Distance(_goalMouth._pointBottomLeft.position, _goalMouth._pointBottomRight.position)
                    : 7.32f;
            }
        }

        public float GoalHeight
        {
            get
            {
                return HasCompleteGoalMouth
                    ? Vector3.Distance(_goalMouth._pointBottomLeft.position, _goalMouth._pointTopLeft.position)
                    : 2.44f;
            }
        }

        public Vector3 PitchForward
        {
            get
            {
                Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up);
                return forward.sqrMagnitude > Mathf.Epsilon ? forward.normalized : Vector3.forward;
            }
        }

        public Vector3 PitchRight
        {
            get
            {
                Vector3 right = Vector3.ProjectOnPlane(transform.right, Vector3.up);
                return right.sqrMagnitude > Mathf.Epsilon ? right.normalized : Vector3.right;
            }
        }

        /// <summary>
        /// Returns goal-space coordinates without depending on the imported model's
        /// local axes: x is lateral (keeper right), y is height and z points into pitch.
        /// </summary>
        public Vector3 WorldToGoalCoordinates(Vector3 worldPosition)
        {
            Vector3 offset = worldPosition - CsvCoordinateOrigin;
            return new Vector3(
                Vector3.Dot(offset, PitchRight),
                Vector3.Dot(offset, Vector3.up),
                Vector3.Dot(offset, PitchForward));
        }

        /// <summary>
        /// Converts a ball-centre position from the CSV coordinate system to Unity world space.
        /// CSV X points from the goal into the pitch, CSV Y points to the goalkeeper's
        /// right (the shooter's left), and CSV Z is height above the pitch. This is the
        /// same handedness as Python world coordinates: (Y forward, X right, Z up).
        /// </summary>
        public Vector3 CsvBallCenterToWorld(Vector3 csvPosition)
        {
            Vector3 pitchForward = PitchForward;
            Vector3 pitchRight = PitchRight;

            return CsvCoordinateOrigin
                + pitchForward * csvPosition.x
                + pitchRight * csvPosition.y
                + Vector3.up * csvPosition.z;
        }

        public Vector3 PenaltySpotBallCenter(float ballRadius)
        {
            return CsvBallCenterToWorld(new Vector3(PenaltySpotDistance, 0f, ballRadius));
        }

        public GoalMouth GoalMouth
        {
            get
            {
                return _goalMouth;
            }

            set
            {
                _goalMouth = value;
            }
        }

        /// <summary>球门线地面中点（两立柱之间）</summary>
        public Vector3 GetGoalLineCentre()
        {
            return (_goalMouth._pointBottomLeft.position + _goalMouth._pointBottomRight.position) * 0.5f;
        }

        public GoalTrigger GoalTrigger
        {
            get
            {
                return _goalTrigger;
            }

            set
            {
                _goalTrigger = value;
            }
        }
       
    }

    [Serializable]
    public struct GoalMouth
    {
        public Transform _pointBottomLeft;
        public Transform _pointBottomRight;
        public Transform _pointTopLeft;
        public Transform _pointTopRight;
    }
}
