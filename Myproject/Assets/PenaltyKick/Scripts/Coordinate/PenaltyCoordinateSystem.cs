using Assets.SuperGoalie.Scripts.Entities;
using UnityEngine;

namespace PenaltyKickPlatform.Coordinate
{
    /// <summary>
    /// Converts penalty CSV coordinates to Unity world coordinates.
    /// CSV origin is the ground point at the center of the goal line.
    /// CSV X points from the goal line toward the penalty spot.
    /// CSV Y points along the goal mouth horizontally: positive is the goalkeeper's
    /// right (and therefore the shooter's left), matching Python world X.
    /// CSV Z is height above the pitch.
    /// </summary>
    public sealed class PenaltyCoordinateSystem
    {
        private readonly Vector3 _origin;
        private readonly Vector3 _xAxis;
        private readonly Vector3 _yAxis;

        public PenaltyCoordinateSystem(Vector3 origin, Vector3 xAxis, Vector3 yAxis)
        {
            _origin = origin;
            _xAxis = Vector3.ProjectOnPlane(xAxis, Vector3.up).normalized;
            _yAxis = Vector3.ProjectOnPlane(yAxis, Vector3.up).normalized;
        }

        public Vector3 Origin { get { return _origin; } }
        public Vector3 XAxis { get { return _xAxis; } }
        public Vector3 YAxis { get { return _yAxis; } }

        public Vector3 ToWorld(Vector3 penaltyPosition)
        {
            return _origin
                + _xAxis * penaltyPosition.x
                + _yAxis * penaltyPosition.y
                + Vector3.up * penaltyPosition.z;
        }

        public static PenaltyCoordinateSystem FromScene(Goal goal, Ball ball)
        {
            Vector3 origin = goal.CsvCoordinateOrigin;
            Vector3 pitchForward = goal.CsvBallCenterToWorld(new Vector3(1f, 0f, 0f)) - origin;
            Vector3 goalRight = goal.CsvBallCenterToWorld(new Vector3(0f, 1f, 0f)) - origin;

            return new PenaltyCoordinateSystem(origin, pitchForward, goalRight);
        }
    }
}
