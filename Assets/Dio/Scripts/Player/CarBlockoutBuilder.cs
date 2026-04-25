using UnityEngine;

namespace Dio.Player
{
    /// Runtime helper that builds a blocked-out car (a body cube + 4 wheel cylinders)
    /// under a parent transform. Cheap, ugly, replaceable with a model later.
    public static class CarBlockoutBuilder
    {
        public struct WheelSetup { public WheelCollider collider; public Transform visual; }

        public static GameObject Build(Transform parent, Color bodyColor)
        {
            var root = new GameObject("CarBlockout");
            if (parent != null) root.transform.SetParent(parent, false);

            // Body
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = new Vector3(1.6f, 0.7f, 3.2f);
            body.transform.localPosition = new Vector3(0, 0.6f, 0);
            body.GetComponent<Renderer>().material.color = bodyColor;
            // Don't keep the box collider — wheel colliders + a low-CG do the work.
            Object.Destroy(body.GetComponent<BoxCollider>());

            // Cabin (visual only).
            var cabin = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cabin.name = "Cabin";
            cabin.transform.SetParent(root.transform, false);
            cabin.transform.localScale = new Vector3(1.2f, 0.5f, 1.4f);
            cabin.transform.localPosition = new Vector3(0, 1.15f, -0.2f);
            cabin.GetComponent<Renderer>().material.color = bodyColor * 0.85f;
            Object.Destroy(cabin.GetComponent<BoxCollider>());

            return root;
        }

        public static WheelSetup BuildWheel(Transform parent, Vector3 localPos, string name)
        {
            var wcGo = new GameObject(name + "_WC");
            wcGo.transform.SetParent(parent, false);
            wcGo.transform.localPosition = localPos;
            var wc = wcGo.AddComponent<WheelCollider>();
            wc.radius = 0.4f;
            wc.suspensionDistance = 0.25f;
            var fwd = wc.forwardFriction;
            fwd.stiffness = 2f; wc.forwardFriction = fwd;
            var side = wc.sidewaysFriction;
            side.stiffness = 2.4f; // high → no spinouts
            wc.sidewaysFriction = side;
            var spring = wc.suspensionSpring;
            spring.spring = 35000; spring.damper = 4500; spring.targetPosition = 0.5f;
            wc.suspensionSpring = spring;

            // Visual.
            var visual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            visual.name = name + "_Vis";
            visual.transform.SetParent(parent, false);
            visual.transform.localPosition = localPos;
            visual.transform.localRotation = Quaternion.Euler(0, 0, 90);
            visual.transform.localScale = new Vector3(0.8f, 0.2f, 0.8f);
            visual.GetComponent<Renderer>().material.color = new Color(0.1f, 0.1f, 0.1f, 1f);
            Object.Destroy(visual.GetComponent<CapsuleCollider>());

            return new WheelSetup { collider = wc, visual = visual.transform };
        }
    }
}
