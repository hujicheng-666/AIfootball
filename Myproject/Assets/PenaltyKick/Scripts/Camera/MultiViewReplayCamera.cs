using System;
using Assets.SuperGoalie.Scripts.Entities;
using PenaltyKickPlatform.Coordinate;
using UnityEngine;
using UnityEngine.EventSystems;

namespace PenaltyKickPlatform.CameraControl
{
    public sealed class MultiViewReplayCamera : MonoBehaviour
    {
        private enum ViewPreset { PenaltySpot, BehindGoal, Side, BallFollow }

        // 场景中主相机默认 FOV 仅为 30°（长焦），在 13~15m 距离下水平可视宽度比
        // 球门(7.32m)还窄，导致球门/点球点/球超出画面。这里统一设为适合本项目的广角。
        private const float DefaultFieldOfView = 55f;

        private Camera _camera;
        private Ball _ball;
        private PenaltyCoordinateSystem _coordinates;
        private ViewPreset _preset;
        private Vector3 _target;
        private float _distance = 18f;
        private float _yaw;
        private float _pitch = 14f;
        private bool _rightDragging;

        public event Action<string> ViewChanged;

        public void Initialize(Camera controlledCamera, Ball ball, PenaltyCoordinateSystem coordinates)
        {
            _camera = controlledCamera;
            _ball = ball;
            _coordinates = coordinates;
            // 显式设置广角 FOV，避免继承场景中过窄的 30° 导致各视角看不到球场全貌
            if (_camera != null)
                _camera.fieldOfView = DefaultFieldOfView;
            ApplyPreset(ViewPreset.PenaltySpot);
        }

        public void CycleView()
        {
            int next = ((int)_preset + 1) % 4;
            ApplyPreset((ViewPreset)next);
        }

        private void Update()
        {
            if (_camera == null) return;

            bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            if (Input.GetMouseButtonDown(1) && !overUI)
                _rightDragging = false;

            if (Input.GetMouseButton(1) && !overUI)
            {
                float dx = Input.GetAxis("Mouse X");
                float dy = Input.GetAxis("Mouse Y");
                if (Mathf.Abs(dx) > 0.01f || Mathf.Abs(dy) > 0.01f)
                {
                    _rightDragging = true;
                    _yaw += dx * 4f;
                    _pitch = Mathf.Clamp(_pitch - dy * 3f, -5f, 80f);
                    UpdateOrbit();
                }
            }

            if (Input.GetMouseButtonUp(1) && !overUI && !_rightDragging)
                CycleView();

            if (!overUI)
            {
                float scroll = Input.mouseScrollDelta.y;
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    _distance = Mathf.Clamp(_distance - scroll * Mathf.Max(0.8f, _distance * 0.08f), 2.5f, 45f);
                    UpdateOrbit();
                }
            }

            if (_preset == ViewPreset.BallFollow && _ball != null)
            {
                _target = _ball.transform.position;
                UpdateOrbit();
            }
        }

        private void ApplyPreset(ViewPreset preset)
        {
            _preset = preset;
            Vector3 origin = _coordinates.Origin;
            Vector3 forwardToPenaltySpot = _coordinates.XAxis;
            Vector3 goalRight = _coordinates.YAxis;

            switch (_preset)
            {
                case ViewPreset.PenaltySpot:
                    _target = origin + forwardToPenaltySpot * 2.5f + Vector3.up * 1.2f;
                    SetPose(origin + forwardToPenaltySpot * 18f + Vector3.up * 4.2f, _target);
                    break;
                case ViewPreset.BehindGoal:
                    _target = origin + forwardToPenaltySpot * 6f + Vector3.up * 1.2f;
                    SetPose(origin - forwardToPenaltySpot * 7f + Vector3.up * 3.2f, _target);
                    break;
                case ViewPreset.Side:
                    _target = origin + forwardToPenaltySpot * 5.5f + Vector3.up * 1.2f;
                    SetPose(origin + forwardToPenaltySpot * 5.5f + goalRight * 14f + Vector3.up * 5f, _target);
                    break;
                default:
                    _target = _ball != null ? _ball.transform.position : origin + forwardToPenaltySpot * 6f + Vector3.up;
                    SetPose(_target + forwardToPenaltySpot * 7f + goalRight * 5f + Vector3.up * 3f, _target);
                    break;
            }

            ViewChanged?.Invoke(GetViewName());
        }

        private void SetPose(Vector3 position, Vector3 target)
        {
            _camera.transform.position = position;
            _camera.transform.rotation = Quaternion.LookRotation((target - position).normalized, Vector3.up);
            _distance = (target - position).magnitude;
            Vector3 euler = _camera.transform.rotation.eulerAngles;
            _yaw = euler.y;
            _pitch = euler.x > 180f ? euler.x - 360f : euler.x;
        }

        private void UpdateOrbit()
        {
            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
            _camera.transform.position = _target - rot * Vector3.forward * _distance;
            _camera.transform.rotation = Quaternion.LookRotation(_target - _camera.transform.position, Vector3.up);
        }

        private string GetViewName()
        {
            switch (_preset)
            {
                case ViewPreset.PenaltySpot:
                    return "Shooter view";
                case ViewPreset.BehindGoal:
                    return "Behind goal";
                case ViewPreset.Side:
                    return "Side view";
                default:
                    return "Ball follow";
            }
        }
    }
}