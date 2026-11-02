using DunkDuel.Sim.Data;
using UnityEngine;
using UnityEngine.Rendering;

namespace DunkDuel.Presentation
{
    /// <summary>Joint angles for one frame of the procedural mannequin (degrees). See <see cref="FighterPoser"/>.</summary>
    public struct Pose
    {
        public float BodyYaw;     // extra yaw on top of facing (spins, crossovers)
        public float Lean;        // spine pitch, + forward
        public float Twist;       // spine yaw
        public float Crouch;      // 0..1 pelvis drop
        public float HipL, HipR;  // thigh pitch, - forward
        public float KneeL, KneeR; // knee flex, + bends the shin back
        public float ShoulderL, ShoulderR; // arm pitch, - raises forward/up
        public float ArmOutL, ArmOutR;     // arm abduction away from the body
        public float ElbowL, ElbowR;       // elbow flex, - bends forward
        public float HeadPitch;
        public float RootY;       // extra vertical offset in meters (falls, cinematic jumps)
        public float RootRoll;    // whole-body pitch backward about the feet (knockdowns), degrees
        public float Squash;      // landing squash 0..1

        public static Pose Lerp(in Pose a, in Pose b, float t)
        {
            return new Pose
            {
                BodyYaw = Mathf.Lerp(a.BodyYaw, b.BodyYaw, t),
                Lean = Mathf.Lerp(a.Lean, b.Lean, t),
                Twist = Mathf.Lerp(a.Twist, b.Twist, t),
                Crouch = Mathf.Lerp(a.Crouch, b.Crouch, t),
                HipL = Mathf.Lerp(a.HipL, b.HipL, t),
                HipR = Mathf.Lerp(a.HipR, b.HipR, t),
                KneeL = Mathf.Lerp(a.KneeL, b.KneeL, t),
                KneeR = Mathf.Lerp(a.KneeR, b.KneeR, t),
                ShoulderL = Mathf.Lerp(a.ShoulderL, b.ShoulderL, t),
                ShoulderR = Mathf.Lerp(a.ShoulderR, b.ShoulderR, t),
                ArmOutL = Mathf.Lerp(a.ArmOutL, b.ArmOutL, t),
                ArmOutR = Mathf.Lerp(a.ArmOutR, b.ArmOutR, t),
                ElbowL = Mathf.Lerp(a.ElbowL, b.ElbowL, t),
                ElbowR = Mathf.Lerp(a.ElbowR, b.ElbowR, t),
                HeadPitch = Mathf.Lerp(a.HeadPitch, b.HeadPitch, t),
                RootY = Mathf.Lerp(a.RootY, b.RootY, t),
                RootRoll = Mathf.Lerp(a.RootRoll, b.RootRoll, t),
                Squash = Mathf.Lerp(a.Squash, b.Squash, t),
            };
        }
    }

    /// <summary>
    /// Graybox mannequin at the fighter's real height, built from primitives with exaggerated hands, shoulders
    /// and calves. Limbs hang from joint pivots so <see cref="Pose"/> angles read naturally in the side view.
    /// </summary>
    public sealed class FighterRig
    {
        public Transform Root;      // at the feet
        public Transform Body;      // facing yaw
        public Transform Hips;
        public Transform Chest;
        public Transform Head;
        public Transform ShoulderL, ShoulderR, ElbowL, ElbowR, HandL, HandR;
        public Transform HipJointL, HipJointR, KneeL, KneeR, FootL, FootR;
        public float Height;
        public float HipHeight;
        public float LegLength;
        public Material Jersey;
        public Material Skin;
        public Material Shorts;
        public Material Glow;
        public Renderer[] Renderers;

        private readonly System.Collections.Generic.List<Renderer> _renderers = new System.Collections.Generic.List<Renderer>();

        public static FighterRig Build(Transform parent, FighterStats stats, FighterVisual v, MaterialLibrary mats, Color primary)
        {
            var rig = new FighterRig();
            float h = stats.Height.ToFloat();
            rig.Height = h;
            float bulk = v.Bulk;
            rig.HipHeight = 0.53f * h;
            float thigh = 0.25f * h, shin = 0.24f * h;
            rig.LegLength = thigh + shin;
            float upperArm = 0.18f * h, forearm = 0.15f * h, hand = 0.075f * h * v.Hands;
            float shoulderHalf = 0.115f * h * v.Shoulders * Mathf.Lerp(1f, bulk, 0.5f);

            rig.Jersey = mats.LitInstance(primary, 0.25f);
            rig.Shorts = mats.LitInstance(v.Secondary, 0.25f);
            rig.Skin = mats.LitInstance(v.Skin, 0.3f);
            rig.Glow = mats.LitInstance(v.Rim, 0.2f);
            MaterialLibrary.SetEmission(rig.Glow, Color.black);
            Material trim = mats.Lit(v.Trim, 0.3f);
            Material hair = mats.Lit(v.Hair, 0.2f);
            Material hairTip = mats.Lit(v.HairTip, 0.4f, 0.3f);
            Material shoes = v.MotifKind == "glitch" ? mats.Emissive(v.Shoes, 1.6f) : mats.Lit(v.Shoes, 0.4f);

            var root = new GameObject("Fighter_" + stats.Def.Id);
            root.transform.SetParent(parent, false);
            rig.Root = root.transform;
            rig.Body = Node("Body", rig.Root, Vector3.zero);
            rig.Hips = Node("Hips", rig.Body, new Vector3(0f, rig.HipHeight, 0f));

            // Pelvis and shorts.
            rig.Part("Shorts", rig.Hips, MeshFactory.Box(new Vector3(0.21f * h * bulk, 0.12f * h, 0.13f * h * bulk), new Vector3(0f, -0.03f * h, 0f)), rig.Shorts);
            Transform spine = Node("Spine", rig.Hips, new Vector3(0f, 0.05f * h, 0f));
            rig.Part("Abdomen", spine, MeshFactory.Box(new Vector3(0.19f * h * bulk, 0.12f * h, 0.12f * h * bulk), new Vector3(0f, 0.05f * h, 0f)), rig.Jersey);
            rig.Chest = Node("Chest", spine, new Vector3(0f, 0.11f * h, 0f));
            rig.Part("Torso", rig.Chest, MeshFactory.Box(new Vector3(shoulderHalf * 1.8f, 0.17f * h, 0.13f * h * bulk), new Vector3(0f, 0.08f * h, 0f)), rig.Jersey);
            rig.Part("Trim", rig.Chest, MeshFactory.Box(new Vector3(shoulderHalf * 1.82f, 0.012f * h, 0.132f * h * bulk), new Vector3(0f, 0.005f * h, 0f)), trim);

            // Head.
            Transform neck = Node("Neck", rig.Chest, new Vector3(0f, 0.17f * h, 0f));
            rig.Part("NeckMesh", neck, MeshFactory.Cylinder(0.028f * h, 0.045f * h, 8, false), rig.Skin);
            rig.Head = Node("Head", neck, new Vector3(0f, 0.045f * h, 0f));
            rig.Part("Skull", rig.Head, MeshFactory.Sphere(0.06f * h, 12, 8), rig.Skin).localPosition = new Vector3(0f, 0.06f * h, 0.005f * h);
            BuildHair(rig, v, h, hair, hairTip, mats);

            // Arms.
            rig.ShoulderL = Node("ShoulderL", rig.Chest, new Vector3(-shoulderHalf, 0.15f * h, 0f));
            rig.ShoulderR = Node("ShoulderR", rig.Chest, new Vector3(shoulderHalf, 0.15f * h, 0f));
            BuildArm(rig, rig.ShoulderL, upperArm, forearm, hand, h, bulk, false, v, mats, out rig.ElbowL, out rig.HandL);
            BuildArm(rig, rig.ShoulderR, upperArm, forearm, hand, h, bulk, true, v, mats, out rig.ElbowR, out rig.HandR);

            // Legs.
            float hipHalf = 0.055f * h * bulk;
            rig.HipJointL = Node("HipL", rig.Hips, new Vector3(-hipHalf, -0.02f * h, 0f));
            rig.HipJointR = Node("HipR", rig.Hips, new Vector3(hipHalf, -0.02f * h, 0f));
            BuildLeg(rig, rig.HipJointL, thigh, shin, h, bulk, v, shoes, out rig.KneeL, out rig.FootL);
            BuildLeg(rig, rig.HipJointR, thigh, shin, h, bulk, v, shoes, out rig.KneeR, out rig.FootR);

            // Kente-style arm sleeve (Kofi), shooting sleeve (Meera), taped hands (Matas) are folded into BuildArm.
            rig.Renderers = rig._renderers.ToArray();
            return rig;
        }

        private static Transform Node(string name, Transform parent, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return go.transform;
        }

        private Transform Part(string name, Transform parent, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.On;
            mr.receiveShadows = true;
            _renderers.Add(mr);
            return go.transform;
        }

        private static void BuildArm(FighterRig rig, Transform shoulder, float upper, float fore, float hand, float h, float bulk, bool right,
            FighterVisual v, MaterialLibrary mats, out Transform elbow, out Transform handT)
        {
            float r = 0.024f * h * Mathf.Lerp(1f, bulk, 0.6f);
            Material sleeve = rig.Skin;
            if (v.MotifKind == "solar" && !right) sleeve = mats.Textured(TextureFactory.Stripes(new[] { v.Primary, Color.black, v.Secondary, v.HairTip, Color.black }, 64, true), Color.white, 0.2f);
            if (v.MotifKind == "storm" && right) sleeve = mats.Lit(v.Secondary, 0.6f, 0.4f);
            rig.Part("UpperArm", shoulder, MeshFactory.Capsule(r * 1.05f, upper + r), sleeve).localPosition = new Vector3(0f, -upper - r * 0.5f, 0f);
            elbow = Node("Elbow", shoulder, new Vector3(0f, -upper, 0f));
            rig.Part("Forearm", elbow, MeshFactory.Capsule(r * 0.9f, fore + r), sleeve).localPosition = new Vector3(0f, -fore - r * 0.5f, 0f);
            handT = Node("Hand", elbow, new Vector3(0f, -fore, 0f));
            Material handMat = v.MotifKind == "seismic" ? mats.Lit(new Color(0.92f, 0.92f, 0.9f), 0.2f) : rig.Skin; // taped fingers
            rig.Part("HandMesh", handT, MeshFactory.Box(new Vector3(hand * 0.55f, hand, hand * 0.3f), new Vector3(0f, -hand * 0.5f, 0f)), handMat);
        }

        private static void BuildLeg(FighterRig rig, Transform hip, float thigh, float shin, float h, float bulk, FighterVisual v, Material shoes,
            out Transform knee, out Transform foot)
        {
            float rt = 0.04f * h * Mathf.Lerp(1f, bulk, 0.7f);
            float rs = 0.03f * h * v.Calves;
            rig.Part("Thigh", hip, MeshFactory.Capsule(rt, thigh + rt), rig.Shorts).localPosition = new Vector3(0f, -thigh - rt * 0.5f, 0f);
            knee = Node("Knee", hip, new Vector3(0f, -thigh, 0f));
            Material shinMat = v.MotifKind == "seismic" ? rig.Shorts : rig.Skin; // knee sleeves
            rig.Part("Shin", knee, MeshFactory.Capsule(rs, shin + rs), shinMat).localPosition = new Vector3(0f, -shin - rs * 0.5f, 0f);
            foot = Node("Foot", knee, new Vector3(0f, -shin, 0f));
            rig.Part("Shoe", foot, MeshFactory.Box(new Vector3(0.05f * h, 0.045f * h, 0.13f * h), new Vector3(0f, -0.02f * h, 0.035f * h)), shoes);
        }

        private static void BuildHair(FighterRig rig, FighterVisual v, float h, Material hair, Material tip, MaterialLibrary mats)
        {
            Transform head = rig.Head;
            float s = 0.062f * h;
            switch (v.HairStyle)
            {
                case "twists":
                    for (int i = 0; i < 9; i++)
                    {
                        float a = i / 9f * Mathf.PI * 2f;
                        Transform t = rig.Part("Twist", head, MeshFactory.Cylinder(s * 0.12f, s * 0.7f, 6, true), hair);
                        t.localPosition = new Vector3(Mathf.Cos(a) * s * 0.55f, s * 1.45f, Mathf.Sin(a) * s * 0.55f);
                        t.localRotation = Quaternion.Euler(Mathf.Sin(a) * 25f, 0f, -Mathf.Cos(a) * 25f);
                        Transform gold = rig.Part("TwistTip", t, MeshFactory.Sphere(s * 0.14f, 6, 4), tip);
                        gold.localPosition = new Vector3(0f, s * 0.7f, 0f);
                    }
                    break;
                case "braid":
                    rig.Part("HairCap", head, MeshFactory.Sphere(s * 1.05f, 12, 8), hair).localPosition = new Vector3(0f, s * 1.08f, -s * 0.05f);
                    for (int i = 0; i < 6; i++)
                    {
                        Transform b = rig.Part("Braid", head, MeshFactory.Sphere(s * 0.22f, 6, 4), i % 2 == 1 ? tip : hair);
                        b.localPosition = new Vector3(0f, s * (0.9f - i * 0.32f), -s * (1.0f + i * 0.08f));
                    }
                    break;
                case "bob":
                    rig.Part("Bob", head, MeshFactory.Sphere(s * 1.12f, 12, 8), hair).localPosition = new Vector3(0f, s * 0.95f, -s * 0.08f);
                    Transform streak = rig.Part("Streak", head, MeshFactory.Box(new Vector3(s * 0.25f, s * 0.9f, s * 0.2f)), mats.Emissive(v.HairTip, 1.2f));
                    streak.localPosition = new Vector3(s * 0.8f, s * 0.8f, s * 0.4f);
                    break;
                default:
                    rig.Part("Crop", head, MeshFactory.Sphere(s * 1.02f, 12, 8), hair).localPosition = new Vector3(0f, s * 1.12f, -s * 0.06f);
                    rig.Part("Beard", head, MeshFactory.Box(new Vector3(s * 1.1f, s * 0.45f, s * 0.6f)), hair).localPosition = new Vector3(0f, s * 0.45f, s * 0.55f);
                    break;
            }
        }

        public void SetShadows(bool on)
        {
            ShadowCastingMode mode = on ? ShadowCastingMode.On : ShadowCastingMode.Off;
            for (int i = 0; i < Renderers.Length; i++) Renderers[i].shadowCastingMode = mode;
        }

        /// <summary>Applies a pose. Facing +1 looks toward the hoop (+X), turned toward the camera for readability.</summary>
        public void Apply(in Pose p, int facing, float cameraTurn)
        {
            float yaw = facing >= 0 ? 90f + cameraTurn : -90f - cameraTurn;
            Body.localRotation = Quaternion.Euler(0f, yaw + p.BodyYaw * (facing >= 0 ? 1f : -1f), 0f) * Quaternion.Euler(-p.RootRoll, 0f, 0f);
            float squash = 1f - p.Squash * 0.08f;
            Body.localScale = new Vector3(1f + p.Squash * 0.05f, squash, 1f + p.Squash * 0.05f);
            float drop = p.Crouch * LegLength * 0.28f;
            Hips.localPosition = new Vector3(0f, HipHeight - drop + p.RootY, 0f);
            Hips.localRotation = Quaternion.Euler(p.Lean * 0.35f, p.Twist * 0.4f, 0f);
            Chest.localRotation = Quaternion.Euler(p.Lean * 0.65f, p.Twist * 0.6f, 0f);
            Head.localRotation = Quaternion.Euler(p.HeadPitch - p.Lean * 0.5f, 0f, 0f);
            ShoulderL.localRotation = Quaternion.Euler(p.ShoulderL, 0f, -p.ArmOutL);
            ShoulderR.localRotation = Quaternion.Euler(p.ShoulderR, 0f, p.ArmOutR);
            ElbowL.localRotation = Quaternion.Euler(p.ElbowL, 0f, 0f);
            ElbowR.localRotation = Quaternion.Euler(p.ElbowR, 0f, 0f);
            // Crouching bends hips and knees; the pose adds its own leg motion on top.
            float crouchHip = -p.Crouch * 55f;
            float crouchKnee = p.Crouch * 95f;
            HipJointL.localRotation = Quaternion.Euler(p.HipL + crouchHip - p.Lean * 0.3f, 0f, 0f);
            HipJointR.localRotation = Quaternion.Euler(p.HipR + crouchHip - p.Lean * 0.3f, 0f, 0f);
            KneeL.localRotation = Quaternion.Euler(p.KneeL + crouchKnee, 0f, 0f);
            KneeR.localRotation = Quaternion.Euler(p.KneeR + crouchKnee, 0f, 0f);
            FootL.localRotation = Quaternion.Euler(-(p.HipL + crouchHip + p.KneeL + crouchKnee) * 0.5f, 0f, 0f);
            FootR.localRotation = Quaternion.Euler(-(p.HipR + crouchHip + p.KneeR + crouchKnee) * 0.5f, 0f, 0f);
        }
    }
}
