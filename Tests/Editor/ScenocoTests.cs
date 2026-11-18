using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Scenoco.Tests
{
    /// <summary>
    /// The engine end to end on Unity's own components. A file's names are Unity's C# properties
    /// (set through their setters) and serialized fields; what it means is checked through the getters.
    /// </summary>
    public class ScenocoTests
    {
        static string Samples => Path.Combine(UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(Files).Assembly).resolvedPath, "Tests/Editor/Samples");

        readonly List<Object> made = new();

        T Import<T>(string text, string source = "test") where T : Object
        {
            var o = (T)Files.Import(text, source);
            made.Add(o);
            return o;
        }

        [TearDown]
        public void Clean()
        {
            foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            made.Clear();
        }

        static Transform Find(GameObject root, string name) => root.GetComponentsInChildren<Transform>(true).Single(t => t.name == name);

        // --- round trip -------------------------------------------------------------------------

        [Test]
        public void BuiltIns_ExportAsTheCanonicalText()
        {
            var source = File.ReadAllText(Path.Combine(Samples, "BuiltIns.uprefab"));
            var expected = File.ReadAllText(Path.Combine(Samples, "BuiltIns.expected.uprefab"));
            Assert.That(Files.Export(Import<GameObject>(source)), Is.EqualTo(expected));
            Assert.That(Files.Export(Import<GameObject>(expected)), Is.EqualTo(expected), "the canonical text reads back as itself");
        }

        [Test]
        public void BuiltIns_MeanWhatUnitysApiSays()
        {
            var rig = Import<GameObject>(File.ReadAllText(Path.Combine(Samples, "BuiltIns.uprefab")));

            var camera = Find(rig, "Camera").GetComponent<Camera>();
            Assert.That(camera.orthographic, Is.True);
            Assert.That(camera.orthographicSize, Is.EqualTo(6.5f));
            Assert.That(camera.allowHDR, Is.False);
            Assert.That(camera.CompareTag("MainCamera"), Is.True);
            Assert.That(Quaternion.Angle(camera.transform.localRotation, Quaternion.Euler(0, 0, 90)), Is.LessThan(0.01f), "450° is a quarter turn");

            var sun = Find(rig, "Sun").GetComponent<Light>();
            Assert.That(sun.shadows, Is.EqualTo(LightShadows.Soft));
            Assert.That(sun.shadowBias, Is.EqualTo(0.2f).Within(1e-6f));

            var crate = Find(rig, "Crate").gameObject;
            Assert.That(crate.GetComponent<MeshFilter>().sharedMesh.name, Is.EqualTo("Cube"), "a built-in mesh, by the reference Unity stores");
            Assert.That(crate.GetComponent<MeshRenderer>().shadowCastingMode, Is.EqualTo(UnityEngine.Rendering.ShadowCastingMode.TwoSided));
            var body = crate.GetComponent<Rigidbody>();
            Assert.That(body.centerOfMass, Is.EqualTo(new Vector3(0, 1, 0)));
            Assert.That(body.automaticCenterOfMass, Is.False, "the centerOfMass setter turns automatic centre of mass off");
            Assert.That(crate.layer, Is.EqualTo(4));
            Assert.That(crate.isStatic, Is.True);
            Assert.That(Find(rig, "Sprite").gameObject.activeSelf, Is.False, "m_IsActive: a stored field no property sets");

            var bar = (RectTransform)Find(rig, "Bar");
            Assert.That(bar.offsetMin, Is.EqualTo(new Vector2(20, -80)), "offsetMin's own setter, after the anchors and pivot");
            Assert.That(bar.offsetMax, Is.EqualTo(new Vector2(-20, 0)));

            Assert.That(Find(rig, "Hud").GetComponent<UnityEngine.UIElements.UIDocument>().sortingOrder, Is.EqualTo(2), "UI Toolkit's UIDocument, built in");

            var hinge = Find(rig, "Door").GetComponent<HingeJoint2D>();
            Assert.That(hinge.motor.maxMotorTorque, Is.EqualTo(100), "a struct property, by its members' C# names");
            Assert.That(hinge.useMotor, Is.True, "the motor setter turns the motor on, as in a script");
            Assert.That(hinge.limits.min, Is.EqualTo(-90));
        }

        // --- the rules --------------------------------------------------------------------------

        static string Fails(string text)
        {
            var e = Assert.Throws<ScenocoException>(() => Files.Import(text, "test"));
            return e.Message;
        }

        [Test]
        public void UnknownField_ListsTheNamesTheTypeHas()
        {
            var m = Fails("<Prefab name=\"P\"><BoxCollider sise=\"1 1 1\" /></Prefab>");
            Assert.That(m, Does.Contain("BoxCollider has no sise — it takes:").And.Contain("size"));
            Assert.That(Fails("<Prefab name=\"P\"><BoxCollider Size=\"1 1 1\" /></Prefab>"), Does.Contain("has no Size"), "names are exact");
            Assert.That(Fails("<Prefab name=\"P\"><GameObject name=\"A\" activeSelf=\"false\" /></Prefab>"),
                Does.Contain("GameObject.activeSelf is read-only").And.Contain("m_IsActive"));
        }

        [Test]
        public void TheHierarchy_IsTheNesting()
        {
            Assert.That(Fails("<Prefab name=\"P\"><GameObject name=\"A\" /><GameObject name=\"B\"><Transform parent=\"#A\" /></GameObject></Prefab>"),
                Does.Contain("Transform.parent changes the hierarchy"));
        }

        [Test]
        public void AQuaternion_IsItsFourNumbers()
        {
            Assert.That(Fails("<Prefab name=\"P\"><Transform localRotation=\"0 90 0\" /></Prefab>"), Does.Contain("x y z w").And.Contain("localEulerAngles"));
            var p = Import<GameObject>("<Prefab name=\"P\"><Transform localEulerAngles=\"0 90 0\" /></Prefab>");
            Assert.That(Quaternion.Angle(p.transform.localRotation, Quaternion.Euler(0, 90, 0)), Is.LessThan(0.01f));
            Assert.That(Files.Export(p), Does.Contain("<Transform localEulerAngles=\"0 90 0\" />"), "read back as the Inspector shows it");
            var turned = Import<GameObject>("<Prefab name=\"P\"><Transform localEulerAngles=\"0 0 450\" /></Prefab>");
            Assert.That(Files.Export(turned), Does.Contain("localEulerAngles=\"0 0 450\""), "the angles as written, as the Inspector keeps them");
            var stored = Import<GameObject>("<Prefab name=\"P\"><Transform localRotation=\"0 0.7071068 0 0.7071068\" /></Prefab>");
            Assert.That(Files.Export(stored), Does.Contain("localEulerAngles=\"0 90 0\""), "a quaternion written reads back in degrees too");
        }

        [Test]
        public void UnknownComponentTagAndLayer_SayWhatExists()
        {
            Assert.That(Fails("<Prefab name=\"P\"><BoxColider /></Prefab>"), Does.Contain("no component is named BoxColider"));
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Hero"));
            Assert.That(Fails("<Prefab name=\"P\"><GameObject name=\"A\" tag=\"Hero\" /></Prefab>"), Does.Contain("GameObject.tag=\"Hero\": Unity says:"), "Unity's own error, at the value");
            Assert.That(Fails("<Prefab name=\"P\"><GameObject name=\"A\" layer=\"Water\" /></Prefab>"), Does.Contain("GameObject.layer"), "a layer is its number");
        }

        [Test]
        public void ErrorsPointAtTheirLine()
        {
            Assert.That(Fails("<Prefab name=\"P\">\n  <GameObject name=\"A\">\n    <BoxCollider sise=\"1 1 1\" />\n  </GameObject>\n</Prefab>"), Does.StartWith("test:3:"));
        }

        // --- references and duplicate names -----------------------------------------------------

        const string TwoEnemies = @"<Prefab name=""Wave"">
  <GameObject name=""Enemy""><Rigidbody /></GameObject>
  <GameObject name=""Enemy"" {0}><Rigidbody /></GameObject>
  <GameObject name=""Anchor""><Rigidbody /><FixedJoint connectedBody=""{1}"" /></GameObject>
</Prefab>";

        [Test]
        public void AmbiguousReference_IsAnError_NeverTheFirstMatch()
        {
            var m = Fails(string.Format(TwoEnemies, "", "#Enemy"));
            Assert.That(m, Does.Contain("ambiguous"));
            Assert.That(m, Does.Contain("id="));
        }

        [Test]
        public void IdentifiedDuplicate_ResolvesAndExportsWithAnId()
        {
            var wave = Import<GameObject>(string.Format(TwoEnemies, "id=\"second\"", "#second"));
            var joint = Find(wave, "Anchor").GetComponent<FixedJoint>();
            Assert.That(joint.connectedBody.transform.GetSiblingIndex(), Is.EqualTo(1), "the second Enemy");

            var text = Files.Export(wave);
            Assert.That(text, Does.Contain("id=\"Enemy-2\""), "export names the duplicate it needs to reference");
            var again = Import<GameObject>(text);
            Assert.That(Find(again, "Anchor").GetComponent<FixedJoint>().connectedBody.transform.GetSiblingIndex(), Is.EqualTo(1));
        }

        // --- a ScriptableObject asset -----------------------------------------------------------

        [Test]
        public void ScriptableObject_RoundTrips()
        {
            const string text = @"<Asset type=""ScenocoTestConfig"" name=""Level1"">
  <ScenocoTestConfig.stages>
    <Stage title=""Opening"" length=""30"" />
    <Stage title=""Rush"" length=""45.5"" />
  </ScenocoTestConfig.stages>
</Asset>
";
            var config = Import<ScenocoTestConfig>(text.Replace(@"name=""Level1"">", @"name=""Level1"" goal=""40"" mode=""Hard"">"));
            Assert.That(config.goal, Is.EqualTo(40));
            Assert.That(config.mode, Is.EqualTo(ScenocoTestConfig.Mode.Hard));
            Assert.That(config.Stages.Select(s => s.title), Is.EqualTo(new[] { "Opening", "Rush" }));
            Assert.That(Files.Export(config), Is.EqualTo(text.Replace(@"name=""Level1"">", @"name=""Level1"" goal=""40"" mode=""Hard"">")));
        }

        [Test]
        public void StructsReferencesAndSwitches_ReadBackAsWritten()
        {
            const string text = "<Prefab name=\"Door\">\n" +
                                "  <GameObject name=\"Frame\">\n    <Rigidbody2D bodyType=\"Static\" />\n  </GameObject>\n" +
                                "  <GameObject name=\"Leaf\">\n" +
                                "    <Rigidbody2D />\n" +
                                "    <HingeJoint2D useMotor=\"true\" useLimits=\"true\" breakForce=\"500\" connectedBody=\"#Frame\">\n" +
                                "      <HingeJoint2D.motor>\n        <JointMotor2D motorSpeed=\"30\" maxMotorTorque=\"100\" />\n      </HingeJoint2D.motor>\n" +
                                "      <HingeJoint2D.limits>\n        <JointAngleLimits2D min=\"-90\" max=\"0\" />\n      </HingeJoint2D.limits>\n" +
                                "    </HingeJoint2D>\n  </GameObject>\n</Prefab>\n";
            var door = Import<GameObject>(text);
            var hinge = Find(door, "Leaf").GetComponent<HingeJoint2D>();
            Assert.That(hinge.connectedBody, Is.EqualTo(Find(door, "Frame").GetComponent<Rigidbody2D>()), "a reference: the object's component the property takes");
            Assert.That(hinge.breakForce, Is.EqualTo(500), "a value whose default is infinity");
            Assert.That(hinge.limits.min, Is.EqualTo(-90));
            Assert.That(hinge.motor.maxMotorTorque, Is.EqualTo(100));
            Assert.That(Files.Export(door), Is.EqualTo(text), "and it reads back as written");
        }

        [Test]
        public void TurnedOffRenderersAndColliders_AreWritten()
        {
            // (Apart: a BoxCollider2D added next to a SpriteRenderer is sized to it by Unity's Reset.)
            const string text = "<Prefab name=\"Ghost\">\n  <SpriteRenderer enabled=\"false\" />\n  <GameObject name=\"Body\">\n    <BoxCollider2D enabled=\"false\" />\n  </GameObject>\n</Prefab>\n";
            var ghost = Import<GameObject>(text);
            Assert.That(ghost.GetComponent<SpriteRenderer>().enabled, Is.False);
            Assert.That(Find(ghost, "Body").GetComponent<BoxCollider2D>().enabled, Is.False);
            Assert.That(Files.Export(ghost), Is.EqualTo(text));
        }

        [Test]
        public void BuiltInResources_AndPathsWithSpaces_RoundTrip()
        {
            const string text = "<Prefab name=\"Panel\">\n" +
                                "  <SpriteRenderer sprite=\"Resources/unity_builtin_extra#UISprite\" />\n" +
                                "  <GameObject name=\"Label\">\n    <MeshRenderer sharedMaterials=\"Library/unity default resources#Font Material, Resources/unity_builtin_extra#Default-Material\" />\n  </GameObject>\n" +
                                "</Prefab>\n";
            var panel = Import<GameObject>(text);
            Assert.That(panel.GetComponent<SpriteRenderer>().sprite.name, Is.EqualTo("UISprite"), "a built-in resource");
            Assert.That(Find(panel, "Label").GetComponent<MeshRenderer>().sharedMaterials.Select(m => m.name), Is.EqualTo(new[] { "Font Material", "Default-Material" }), "references with spaces in a list");
            Assert.That(Files.Export(panel), Is.EqualTo(text));
        }

        [Test]
        public void ReferencesUnityStoresUnderOtherNames_AreWritten()
        {
            var go = Import<GameObject>("<Prefab name=\"Speaker\">\n  <AudioSource />\n  <Animator />\n</Prefab>\n");
            var clip = AudioClip.Create("beep", 64, 1, 44100, false);
            var controller = new UnityEditor.Animations.AnimatorController();
            made.Add(clip);
            made.Add(controller);
            go.GetComponent<AudioSource>().clip = clip;
            go.GetComponent<Animator>().runtimeAnimatorController = controller;
            var text = Files.Export(go);
            // Not assets on disk, so they can't be named; what matters is that they're found under the
            // properties that store them: AudioSource.resource (m_Resource, which clip also sets) and
            // Animator.runtimeAnimatorController (m_Controller).
            Assert.That(text, Does.Contain("<AudioSource resource=").And.Contain("<Animator runtimeAnimatorController="));
        }

        [Test]
        public void RuntimeState_IsRefused_BecauseNothingStoresIt()
        {
            Assert.That(Fails("<Prefab name=\"P\"><Rigidbody linearVelocity=\"0 1 0\" /></Prefab>"), Does.Contain("Rigidbody.linearVelocity is runtime state"));
        }

        [Test]
        public void AValueUnityChangesAsItsApplied_IsReportedAtItsLine()
        {
            UnityEngine.TestTools.LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(@"C\.uprefab:2: Camera\.fieldOfView=""500"" became ""179"""));
            var go = Import<GameObject>("<Prefab name=\"C\">\n  <Camera fieldOfView=\"500\" />\n</Prefab>\n", "C.uprefab");
            Assert.That(go.GetComponent<Camera>().fieldOfView, Is.EqualTo(179), "the setter's own range");
        }

        // --- reading any asset (Files.Describe) ----------------------------------------------------------

        [Test]
        public void Describe_AMaterial_WritesTheShadersPropertiesThatDiffer()
        {
            var m = new Material(Shader.Find("Sprites/Default")) { name = "Glow" };    // in every render pipeline
            try
            {
                m.SetColor("_Color", Color.red);
                m.SetTextureScale("_MainTex", new Vector2(2, 2));
                Assert.That(Files.Describe(m), Is.EqualTo(
                    "<Asset type=\"Material\" name=\"Glow\" shader=\"Sprites/Default\" _MainTex_ST=\"2 2 0 0\" _Color=\"1 0 0 1\" />\n"));
            }
            finally { Object.DestroyImmediate(m); }
        }

        [Test]
        public void Describe_AnAnimationClip_WritesItsCurvesAndEvents()
        {
            var clip = new AnimationClip { name = "Bob" };
            try
            {
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("Body", typeof(SpriteRenderer), "m_Color.a"), AnimationCurve.Constant(0, 1, 0.5f));
                AnimationUtility.SetAnimationEvents(clip, new[] { new AnimationEvent { time = 0.5f, functionName = "Step", intParameter = 2 } });
                Assert.That(Files.Describe(clip), Is.EqualTo(
                    "<Asset type=\"AnimationClip\" name=\"Bob\" length=\"1\">\n" +
                    "  <Curve path=\"Body\" type=\"SpriteRenderer\" property=\"m_Color.a\">\n" +
                    "    <Key time=\"0\" value=\"0.5\" />\n    <Key time=\"1\" value=\"0.5\" />\n  </Curve>\n" +
                    "  <Event time=\"0.5\" function=\"Step\" int=\"2\" />\n</Asset>\n"));
            }
            finally { Object.DestroyImmediate(clip); }
        }
    }

    public class ScenocoTestConfig : ScriptableObject
    {
        public enum Mode { Easy, Hard }

        [Serializable]
        public class Stage
        {
            public string title;
            public float length;
        }

        public int goal;
        public Mode mode;
        [SerializeField] List<Stage> stages = new();
        public IReadOnlyList<Stage> Stages => stages;
    }
}
