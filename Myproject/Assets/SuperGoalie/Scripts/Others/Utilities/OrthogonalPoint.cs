using UnityEngine;

namespace Assets.SuperGoalie.Scripts.Others.Utilities
{
    public static class OrthogonalPoint
    {
        /// <summary>
        /// Given two points from and target, this function calculates the direction vector starting at point to destination target
        /// and returns a point on Vector from->target such that Vector point->p is orthogonal to Vector3 from->target
        /// </summary>
        /// <returns>The point.</returns>
        /// <param name="from">From.</param>
        /// <param name="target">Target.</param>
        /// <param name="point">Point.</param>
        public static Vector3 OrthPoint(Vector3 from, Vector3 target, Vector3 point)
        {

            //using Cos theta = adj/hyp;
            //we find adj = hyp * Cos theta;
            //return  point x =  from + dirToTarget.normaized * adj
            return from + ((target - from).normalized * (Vector3.Distance(from, point) * Mathf.Cos(Mathf.Deg2Rad * Vector3.Angle(target - from, point - from))));
        }
    }
}
