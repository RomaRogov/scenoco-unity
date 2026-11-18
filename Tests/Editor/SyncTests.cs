using System.Xml.Linq;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Scenoco.Tests
{
    /// <summary>
    /// A scene and its .uscene twin kept in step: the first text, a save written into the file in
    /// place, an edited file applied to an open or a closed scene, and each answer to a conflict.
    /// Scenes are made in a throwaway folder and opened next to whatever is open; values are
    /// changed as the Inspector changes them (SerializedObject) and checked through Unity's getters.
    /// </summary>
    public class SyncTests : TwinFixture
    {
        const string Level = @"<?xml version=""1.0""?>
<!-- Level for the sync tests -->
<Scene name=""Level"">
  <!-- the player -->
  <GameObject name=""Ball"">
    <Transform localPosition=""1 2 0"" />   
    <Rigidbody2D mass=""2.0"" gravityScale=""0"" />
  </GameObject>

  <!-- walls -->
  <GameObject name=""Wall"">
    <Transform localPosition=""5 0 0"" />
    <BoxCollider2D size=""1 10"" />
  </GameObject>
  <GameObject name=""Old"" />
</Scene>
";

        // --- never the scene a person has open ---------------------------------------------------------

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator NothingMarksTheActiveSceneModified()
        {
            var active = SceneManager.GetActiveScene();
            Assume.That(active.isDirty, Is.False);
            var prefab = (GameObject)Files.Import("<Prefab name=\"P\"><Rigidbody2D mass=\"2\" /><GameObject name=\"Child\" /></Prefab>");
            Files.Export(prefab);
            Object.DestroyImmediate(prefab);
            FromText("Quiet", Level);                                       // a scene created from text
            File.WriteAllText(textPath, Level.Replace("name=\"Old\"", "name=\"Old\" />\n  <GameObject name=\"Added\""));
            Twins.Sync();                                               // an object added to a closed scene
            yield return null;                                               // Unity marks a scene modified at the end of the frame
            yield return null;
            Assert.That(active.isDirty, Is.False);
        }

        // --- a change is exactly what was edited -----------------------------------------------------

        [Test]
        public void AComponentHasOneKey_HoweverAndWheneverItsTypeIsWritten()
        {
            // Types nothing else here uses, so the first call is the first time each is seen.
            foreach (var (shortName, fullName) in new[] { ("WindZone", "UnityEngine.WindZone"), ("LensFlare", "UnityEngine.LensFlare"), ("Skybox", "UnityEngine.Skybox") })
            {
                string Key(string written) => SceneDiff.Components(new XElement("GameObject", new XElement(written))).Single().key;
                var first = Key(shortName);
                Assert.That(Key(shortName), Is.EqualTo(first), "the same key the second time");
                Assert.That(Key(fullName), Is.EqualTo(first), "and by its full name");
            }
        }

        [Test]
        public void EditingOneValue_IsOneChange_AndTouchesNothingElse()
        {
            const string text = @"<Scene name=""One"">
  <GameObject name=""Eye"">
    <Camera orthographic=""true"" />
    <AudioListener />
  </GameObject>
  <GameObject name=""Lamp"">
    <Transform localPosition=""0 3 0"" />
    <Light intensity=""2"" />
  </GameObject>
</Scene>
";
            var twin = FromText("One", text);
            var before = new KeyedTree(Markup.Parse(text));
            foreach (var e in before.Objects()) before.Keys[e] = e.Attr("name");
            var edited = TreeMatcher.Match(before, Markup.Parse(text.Replace("0 3 0", "0 4 0").Replace("<Scene name", "<!-- a note -->\n<Scene name")));
            Assert.That(SceneDiff.Diff(before, edited).Select(c => c.ToString()), Is.EqualTo(new[] { "Lamp|UnityEngine.Transform#0.localPosition = 0 4 0" }));

            File.WriteAllText(textPath, text.Replace("0 3 0", "0 4 0"));
            Twins.Sync();
            Assert.That(twin.Message, Does.Contain("applied 1 change").And.Contain("1 SetAttribute"));
            var scene = OpenScene();
            Assert.That(Get(scene, "Eye").GetComponent<Camera>(), Is.Not.Null);
            Assert.That(Get(scene, "Eye").GetComponent<AudioListener>(), Is.Not.Null);
            Assert.That(Get(scene, "Lamp").GetComponent<Light>().intensity, Is.EqualTo(2));
        }

        // --- the document ---------------------------------------------------------------------------

        [Test]
        public void Markup_PrintsUnchangedTextAsItWasWritten()
        {
            Assert.That(Markup.Print(Markup.Parse(Level)), Is.EqualTo(Level), "comments, blank lines, indentation, trailing spaces");
            Assert.That(Markup.Print(Markup.Parse("<A  x='1'\n   y=\"2\"/>")), Is.EqualTo("<A x=\"1\" y=\"2\" />\n"), "the library's one normalization: attribute quoting and spacing");
        }

        [Test]
        public void Markup_ChangesOnlyWhatChanged()
        {
            var root = Markup.Parse(Level);
            var objects = root.Elements().ToList();
            objects[0].Element("Rigidbody2D").Attribute("gravityScale").Value = "3";
            Markup.Remove(objects[2]);                                                     // Old
            Markup.Insert(objects[1], new XElement("GameObject", new XAttribute("name", "Brick")));
            var expected = Level.Replace("gravityScale=\"0\"", "gravityScale=\"3\"")
                .Replace("  <GameObject name=\"Old\" />\n", "")
                .Replace("    <BoxCollider2D size=\"1 10\" />\n", "    <BoxCollider2D size=\"1 10\" />\n    <GameObject name=\"Brick\" />\n");
            Assert.That(Markup.Print(root), Is.EqualTo(expected));
        }

        // --- the first text ----------------------------------------------------------------------------

        [Test]
        public void Track_WritesOnlyWhatDiffersFromAFreshObject()
        {
            scenePath = $"{Folder}/Tracked.unity";
            textPath = $"{Folder}/Tracked.uscene";
            File.WriteAllText(scenePath, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n");     // an empty scene
            AssetDatabase.ImportAsset(scenePath);
            var scene = OpenScene();
            var ball = ObjectFactory.CreateGameObject(scene, HideFlags.None, "Ball");
            Edit(ball.transform, "m_LocalPosition", p => p.vector3Value = new Vector3(1, 2, 0));
            Edit(ObjectFactory.AddComponent<Rigidbody2D>(ball), "m_Mass", p => p.floatValue = 2);
            ObjectFactory.CreateGameObject(scene, HideFlags.None, "Empty");
            Save(scene);

            Files.Track(scenePath);

            Assert.That(Text, Is.EqualTo(
                "<Scene name=\"Tracked\">\n" +
                "  <GameObject name=\"Ball\">\n" +
                "    <Transform localPosition=\"1 2 0\" />\n" +
                "    <Rigidbody2D mass=\"2\" />\n" +
                "  </GameObject>\n" +
                "  <GameObject name=\"Empty\" />\n" +
                "</Scene>\n"));
            Assert.That(scene.isDirty, Is.False, "making the text doesn't touch the scene");
        }

        // --- scene → file ------------------------------------------------------------------------------

        [Test]
        public void Save_WritesChangesIntoTheFileInPlace()
        {
            FromText("Saved", Level);
            var scene = OpenScene();
            Edit(Get(scene, "Ball").GetComponent<Rigidbody2D>(), "m_GravityScale", p => p.floatValue = 1);   // back to its default
            ObjectFactory.AddComponent<CircleCollider2D>(Get(scene, "Ball"));
            Edit(Get(scene, "Wall").transform, "m_LocalPosition", p => p.vector3Value = new Vector3(6, 0, 0));
            var brick = ObjectFactory.CreateGameObject(scene, HideFlags.None, "Brick");
            brick.transform.SetParent(Get(scene, "Wall").transform, false);
            ObjectFactory.AddComponent<BoxCollider2D>(brick);
            Edit(Get(scene, "Old"), "m_Name", p => p.stringValue = "New");
            Save(scene);

            Assert.That(Text, Is.EqualTo(@"<?xml version=""1.0""?>
<!-- Level for the sync tests -->
<Scene name=""Level"">
  <!-- the player -->
  <GameObject name=""Ball"">
    <Transform localPosition=""1 2 0"" />   
    <Rigidbody2D mass=""2.0"" />
    <CircleCollider2D />
  </GameObject>

  <!-- walls -->
  <GameObject name=""Wall"">
    <Transform localPosition=""6 0 0"" />
    <BoxCollider2D size=""1 10"" />
    <GameObject name=""Brick"">
      <BoxCollider2D />
    </GameObject>
  </GameObject>
  <GameObject name=""New"" />
</Scene>
"));
            Assert.That(Twins.Find(textPath).Status, Is.EqualTo(TwinStatus.Synced));
        }

        [Test]
        public void AValueTheFileWritesUnderAnotherName_IsReplacedWhereItWas()
        {
            const string text = "<Scene name=\"Turned\">\n  <GameObject name=\"Arm\">\n    <Transform localRotation=\"0 0.7071068 0 0.7071068\" localScale=\"2 2 2\" />\n  </GameObject>\n</Scene>\n";
            FromText("Turned", text);
            var scene = OpenScene();
            Edit(Get(scene, "Arm").transform, "m_LocalRotation", p => p.quaternionValue = Quaternion.Euler(0, 45, 0));
            Save(scene);
            Assert.That(Text, Is.EqualTo(text.Replace("localRotation=\"0 0.7071068 0 0.7071068\"", "localEulerAngles=\"0 45 0\"")),
                "localRotation and localEulerAngles store the same field: the one a rotation reads back as, in its place");
        }

        [Test]
        public void ARotationTurnedInTheInspector_IsWrittenInDegrees()
        {
            const string text = "<Scene name=\"Spun\">\n  <GameObject name=\"Arm\">\n    <Transform localEulerAngles=\"0 90 0\" localScale=\"2 2 2\" />\n  </GameObject>\n</Scene>\n";
            FromText("Spun", text);
            var scene = OpenScene();
            var arm = Get(scene, "Arm").transform;
            Undo.RecordObject(arm, "Rotate");
            TransformUtils.SetInspectorRotation(arm, new Vector3(0, 405, 0));       // as the Inspector's Rotation field sets it
            Save(scene);
            Assert.That(Text, Is.EqualTo(text.Replace("0 90 0", "0 405 0")));
        }

        // --- file → scene ------------------------------------------------------------------------------

        [Test]
        public void EditedFile_IsAppliedToTheOpenScene_AsOneUndoStep()
        {
            FromText("Applied", Level);
            var scene = OpenScene();
            var edited = Level.Replace("mass=\"2.0\"", "mass=\"3\"")
                .Replace("  <GameObject name=\"Old\" />\n", "  <GameObject name=\"Old\" />\n  <GameObject name=\"Extra\">\n    <Transform localPosition=\"0 1 0\" />\n  </GameObject>\n")
                .Replace("  <GameObject name=\"Wall\">\n    <Transform localPosition=\"5 0 0\" />\n    <BoxCollider2D size=\"1 10\" />\n  </GameObject>\n", "");
            File.WriteAllText(textPath, edited);
            Twins.Sync();

            Assert.That(Get(scene, "Ball").GetComponent<Rigidbody2D>().mass, Is.EqualTo(3));
            Assert.That(Get(scene, "Extra").transform.localPosition, Is.EqualTo(new Vector3(0, 1, 0)));
            Assert.That(Get(scene, "Wall"), Is.Null);
            Assert.That(scene.isDirty, Is.False, "applied and saved");
            Assert.That(Text, Is.EqualTo(edited), "the file is never rewritten for an edit that came from it");

            Undo.PerformUndo();
            Assert.That(Get(scene, "Wall"), Is.Not.Null, "one undo step brings it all back");
            Assert.That(Get(scene, "Ball").GetComponent<Rigidbody2D>().mass, Is.EqualTo(2));
            Assert.That(Get(scene, "Extra"), Is.Null);
        }

        [Test]
        public void WindowsLineEndings_AreReadAndKept()
        {
            string Crlf(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            FromText("Windows", Crlf(Level));
            File.WriteAllText(textPath, Crlf(Level.Replace("localPosition=\"5 0 0\"", "localPosition=\"8 0 0\"")));
            Twins.Sync();
            var scene = OpenScene();
            Assert.That(Get(scene, "Wall").transform.localPosition.x, Is.EqualTo(8), Twins.Status());

            Edit(Get(scene, "Wall").transform, "m_LocalPosition", p => p.vector3Value = new Vector3(9, 0, 0));
            Save(scene);
            Assert.That(Text, Is.EqualTo(Crlf(Level.Replace("localPosition=\"5 0 0\"", "localPosition=\"9 0 0\""))), "written back in place, with the file's line endings");
            Assert.That(Twins.Find(textPath).Status, Is.EqualTo(TwinStatus.Synced));
        }

        [Test]
        public void EditedFile_IsAppliedToAClosedScene()
        {
            FromText("Closed", Level);
            File.WriteAllText(textPath, Level.Replace("localPosition=\"5 0 0\"", "localPosition=\"8 0 0\"").Replace("name=\"Old\"", "name=\"Renamed\""));
            Twins.Sync();
            var scene = OpenScene();
            Assert.That(Get(scene, "Wall").transform.localPosition.x, Is.EqualTo(8));
            Assert.That(Get(scene, "Renamed"), Is.Not.Null);
            Assert.That(Get(scene, "Old"), Is.Null);
        }

        [Test]
        public void InvalidFile_IsReportedWithItsLine_AndNotApplied()
        {
            FromText("Invalid", Level);
            File.WriteAllText(textPath, Level.Replace("<BoxCollider2D size=\"1 10\" />", "<BoxColider2D />"));
            var status = Twins.Sync();
            Assert.That(status, Does.Contain("Invalid").And.Contain("Invalid.uscene:13").And.Contain("BoxColider2D"));
            var scene = OpenScene();
            Assert.That(Get(scene, "Wall").GetComponent<BoxCollider2D>(), Is.Not.Null);
        }

        // --- both changed ----------------------------------------------------------------------------

        /// <summary>The scene with an unsaved change (Ball moved to 9 9) and the file edited (<paramref name="edit"/>).</summary>
        Scene BothChanged(string name, System.Func<string, string> edit)
        {
            FromText(name, Level);
            var scene = OpenScene();
            Edit(Get(scene, "Ball").transform, "m_LocalPosition", p => p.vector3Value = new Vector3(9, 9, 0));
            File.WriteAllText(textPath, edit(Level));
            Twins.Sync();
            Assert.That(Twins.Find(textPath).Status, Is.EqualTo(TwinStatus.Conflict));
            Assert.That(Get(scene, "Ball").transform.localPosition, Is.EqualTo(new Vector3(9, 9, 0)), "nothing applied before a choice");
            return scene;
        }

        [Test]
        public void Conflict_Merge_CombinesChangesToDifferentThings()
        {
            var scene = BothChanged("Merged", t => t.Replace("localPosition=\"5 0 0\"", "localPosition=\"7 0 0\"").Replace("<!-- walls -->", "<!-- walls, moved -->"));
            Files.Resolve(textPath, "merge");

            Assert.That(Twins.Find(textPath).Status, Is.EqualTo(TwinStatus.Synced));
            Assert.That(Get(scene, "Ball").transform.localPosition, Is.EqualTo(new Vector3(9, 9, 0)));
            Assert.That(Get(scene, "Wall").transform.localPosition.x, Is.EqualTo(7));
            Assert.That(scene.isDirty, Is.False);
            Assert.That(Text, Is.EqualTo(Level.Replace("localPosition=\"5 0 0\"", "localPosition=\"7 0 0\"").Replace("<!-- walls -->", "<!-- walls, moved -->")
                .Replace("localPosition=\"1 2 0\"", "localPosition=\"9 9 0\"")));
        }

        [Test]
        public void Conflict_Merge_MarksChangesToTheSameThing_AndTheResolutionIsApplied()
        {
            var scene = BothChanged("Marked", t => t.Replace("localPosition=\"1 2 0\"", "localPosition=\"4 4 0\"").Replace("localPosition=\"5 0 0\"", "localPosition=\"7 0 0\""));
            Files.Resolve(textPath, "merge");

            Assert.That(Twins.Find(textPath).Status, Is.EqualTo(TwinStatus.Merging));
            Assert.That(Get(scene, "Wall").transform.localPosition.x, Is.EqualTo(7), "the change that doesn't clash is merged");
            Assert.That(Text, Does.Contain("<<<<<<< Marked (your changes)\n    <Transform localPosition=\"9 9 0\" />   \n=======\n    <Transform localPosition=\"4 4 0\" />   \n>>>>>>> Marked.uscene\n"));

            // Resolving is an edit of the file: keep the file's side.
            var lines = Text.Split('\n').ToList();
            var start = lines.FindIndex(l => l.StartsWith("<<<<<<<"));
            lines.RemoveRange(start, 3);
            lines.RemoveAt(lines.FindIndex(l => l.StartsWith(">>>>>>>")));
            File.WriteAllText(textPath, string.Join("\n", lines));
            Twins.Sync();

            Assert.That(Twins.Find(textPath).Status, Is.EqualTo(TwinStatus.Synced));
            Assert.That(Get(scene, "Ball").transform.localPosition, Is.EqualTo(new Vector3(4, 4, 0)));
            Assert.That(scene.isDirty, Is.False);
        }

        [Test]
        public void Conflict_Keep_RewritesTheFileFromTheScene()
        {
            var scene = BothChanged("Kept", t => t.Replace("localPosition=\"5 0 0\"", "localPosition=\"7 0 0\""));
            Files.Resolve(textPath, "keep");
            Assert.That(Get(scene, "Wall").transform.localPosition.x, Is.EqualTo(5));
            Assert.That(Text, Is.EqualTo(Level.Replace("localPosition=\"1 2 0\"", "localPosition=\"9 9 0\"")));
            Assert.That(scene.isDirty, Is.False);
        }

        [Test]
        public void Conflict_TakeFile_ReplacesTheScenesUnsavedChanges()
        {
            var scene = BothChanged("Taken", t => t.Replace("localPosition=\"5 0 0\"", "localPosition=\"7 0 0\""));
            Files.Resolve(textPath, "file");
            Assert.That(Get(scene, "Ball").transform.localPosition, Is.EqualTo(new Vector3(1, 2, 0)));
            Assert.That(Get(scene, "Wall").transform.localPosition.x, Is.EqualTo(7));
            Assert.That(Text, Is.EqualTo(Level.Replace("localPosition=\"5 0 0\"", "localPosition=\"7 0 0\"")));
            Assert.That(scene.isDirty, Is.False);
        }

        [Test]
        public void Describe_AnAssetOnDisk_AndImportSettings()
        {
            AssetDatabase.CreateAsset(new PhysicsMaterial2D { bounciness = 1, friction = 0 }, $"{Folder}/BallPhysics.physicsMaterial2D");
            Assert.That(Files.Describe($"{Folder}/BallPhysics.physicsMaterial2D"),
                Is.EqualTo("<Asset type=\"PhysicsMaterial2D\" name=\"BallPhysics\" bounciness=\"1\" friction=\"0\" />\n"));

            // A twin keeps the order its text was written in; Describe writes serialized order. Same content:
            static string Content(string text)
            {
                var root = Markup.Plain(Markup.Parse(text));
                foreach (var e in root.DescendantsAndSelf()) e.ReplaceAttributes(e.Attributes().OrderBy(a => a.Name.LocalName).ToList());
                return Markup.Canonical(root);
            }
            FromText("Described", "<Scene name=\"Described\">\n  <GameObject name=\"Ball\">\n    <Transform localPosition=\"0 3 0\" />\n    <Rigidbody2D gravityScale=\"0\" mass=\"2\" />\n  </GameObject>\n</Scene>\n");
            Assert.That(Content(Files.Describe(scenePath)), Is.EqualTo(Content(File.ReadAllText(textPath))), "a scene as its twin says it");

            File.WriteAllBytes($"{Folder}/Pixel.png", new Texture2D(1, 1).EncodeToPNG());
            AssetDatabase.ImportAsset($"{Folder}/Pixel.png");
            Assert.That(Files.Describe(AssetImporter.GetAtPath($"{Folder}/Pixel.png")), Does.StartWith("<Asset type=\"TextureImporter\""));
        }
    }
}
