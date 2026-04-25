using System;
using System.Collections.Generic;
using UnityEngine;

namespace Dio.Level
{
    [Serializable]
    public struct TrackPoint
    {
        [Tooltip("Unit vector from planet center. Position = direction * planetRadius.")]
        public Vector3 directionFromCenter;
        [Tooltip("Banking angle in degrees, used by the future track-mesh sweep.")]
        public float bank;
    }

    [CreateAssetMenu(menuName = "Dio/Level Data", fileName = "NewLevel")]
    public class LevelData : ScriptableObject
    {
        public float planetRadius = 200f;
        public int seed = 12345;
        public float trackWidth = 15f;
        public List<TrackPoint> points = new List<TrackPoint>();

        public bool HasMinimum => points.Count >= 2;
        public TrackPoint Start => points.Count > 0 ? points[0] : default;
        public TrackPoint Finish => points.Count > 0 ? points[points.Count - 1] : default;

        public Vector3 PositionOf(int i) =>
            (points[i].directionFromCenter.sqrMagnitude > 0f
                ? points[i].directionFromCenter.normalized
                : Vector3.up) * planetRadius;
    }
}
