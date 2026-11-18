using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Scenoco.Tests
{
    /// <summary>
    /// Prefabs: a prefab and its .uprefab twin kept in step both ways, and prefabs placed in a scene,
    /// where the text holds only each placement's overrides.
    /// </summary>
    public class PrefabTests : TwinFixture
    {
        const string Enemy = @"<Prefab name=""Enemy"">
  <!-- the body -->
  <Rigidbody2D mass=""2"" />
  <GameObject name=""Muzzle"">
    <Transform localPosition=""0 1 0"" />
  </GameObject>
</Prefab>
";

        const string EnemyPath = Folder + "/Enemy.prefab";

        static GameObject Asset(string path = EnemyPath) => AssetDatabase.LoadAssetAtPath<GameObject>(path);

        static long FileId(Object o) => AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out _, out long id) ? id : 0;

        /// <summary>Change a prefab the way Prefab Mode does: load its contents, edit, save.</summary>
        static void EditPrefab(string path, System.Action<GameObject> edit)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            edit(root);
            PrefabUtility.SaveAsPrefabAsset(root, path);
            PrefabUtility.UnloadPrefabContents(root);
        }

        // --- a prefab and its twin -----------------------------------------------------------------------

        [Test]
        public void Prefab_IsCreatedFromItsText()
        {
            PrefabFromText("Enemy", Enemy);
            var enemy = Asset();
            Assert.That(enemy.name, Is.EqualTo("Enemy"));
            Assert.That(enemy.GetComponent<Rigidbody2D>().mass, Is.EqualTo(2));
            Assert.That(enemy.transform.Find("Muzzle").localPosition, Is.EqualTo(new Vector3(0, 1, 0)));
        }

        [Test]
        public void EditedText_ChangesThePrefab_AndObjectsKeepTheirFileIds()
        {
            var twin = PrefabFromText("Enemy", Enemy);
            var muzzle = FileId(Asset().transform.Find("Muzzle").gameObject);
            var body = FileId(Asset().GetComponent<Rigidbody2D>());

            File.WriteAllText(twin.TextPath, Enemy.Replace("mass=\"2\"", "mass=\"3\"").Replace("</Prefab>", "  <GameObject name=\"Flag\" />\n</Prefab>"));
            Twins.Sync();

            Assert.That(twin.Message, Does.Contain("applied 2 changes").And.Contain("1 AddObject").And.Contain("1 SetAttribute"));
            Assert.That(Asset().GetComponent<Rigidbody2D>().mass, Is.EqualTo(3));
            Assert.That(Asset().transform.Find("Flag"), Is.Not.Null);
            Assert.That(FileId(Asset().transform.Find("Muzzle").gameObject), Is.EqualTo(muzzle), "placements keep pointing at the same objects");
            Assert.That(FileId(Asset().GetComponent<Rigidbody2D>()), Is.EqualTo(body));
        }

        [Test]
        public void SavedPrefab_IsWrittenIntoItsTextInPlace()
        {
            var twin = PrefabFromText("Enemy", Enemy);
            EditPrefab(EnemyPath, root =>
            {
                Edit(root.transform.Find("Muzzle"), "m_LocalPosition", p => p.vector3Value = new Vector3(0, 2, 0));
                ObjectFactory.AddComponent<BoxCollider2D>(root);
            });
            Twins.Sync();

            Assert.That(File.ReadAllText(twin.TextPath), Is.EqualTo(@"<Prefab name=""Enemy"">
  <!-- the body -->
  <Rigidbody2D mass=""2"" />
  <BoxCollider2D />
  <GameObject name=""Muzzle"">
    <Transform localPosition=""0 2 0"" />
  </GameObject>
</Prefab>
"));
        }

        [Test]
        public void Track_WritesAPrefabsFirstText()
        {
            PrefabFromText("Enemy", Enemy);
            File.Delete(Folder + "/Enemy.uprefab");
            Twins.Sync();
            Files.Track(EnemyPath);
            Assert.That(File.ReadAllText(Folder + "/Enemy.uprefab"), Is.EqualTo(
                "<Prefab name=\"Enemy\">\n  <Rigidbody2D mass=\"2\" />\n  <GameObject name=\"Muzzle\">\n    <Transform localPosition=\"0 1 0\" />\n  </GameObject>\n</Prefab>\n"));
        }

        // --- a prefab open in Prefab Mode ------------------------------------------------------------

        [TearDown]
        public void LeavePrefabMode() => UnityEditor.SceneManagement.StageUtility.GoToMainStage();

        [Test]
        public void EditedText_IsAppliedToThePrefabOpenInPrefabMode()
        {
            var twin = PrefabFromText("Enemy", Enemy);
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(EnemyPath);
            File.WriteAllText(twin.TextPath, Enemy.Replace("mass=\"2\"", "mass=\"4\""));
            Twins.Sync();

            Assert.That(twin.Status, Is.EqualTo(TwinStatus.Synced), twin.Message);
            Assert.That(stage.prefabContentsRoot.GetComponent<Rigidbody2D>().mass, Is.EqualTo(4), "the objects being edited");
            Assert.That(stage.scene.isDirty, Is.False, "and saved");
            Assert.That(Asset().GetComponent<Rigidbody2D>().mass, Is.EqualTo(4), "the asset");
        }

        [Test]
        public void UnsavedPrefabModeEdits_AndAnEditedFile_AreAConflict_ThatMerges()
        {
            var twin = PrefabFromText("Enemy", Enemy);
            var stage = UnityEditor.SceneManagement.PrefabStageUtility.OpenPrefab(EnemyPath);
            Edit(stage.prefabContentsRoot.transform.Find("Muzzle"), "m_LocalPosition", p => p.vector3Value = new Vector3(0, 9, 0));
            File.WriteAllText(twin.TextPath, Enemy.Replace("mass=\"2\"", "mass=\"4\""));
            Twins.Sync();
            Assert.That(twin.Status, Is.EqualTo(TwinStatus.Conflict));
            Assert.That(stage.prefabContentsRoot.GetComponent<Rigidbody2D>().mass, Is.EqualTo(2), "nothing applied before a choice");

            Files.Resolve(twin.TextPath, "merge");
            Assert.That(twin.Status, Is.EqualTo(TwinStatus.Synced), twin.Message);
            Assert.That(Asset().GetComponent<Rigidbody2D>().mass, Is.EqualTo(4));
            Assert.That(Asset().transform.Find("Muzzle").localPosition, Is.EqualTo(new Vector3(0, 9, 0)));
            Assert.That(File.ReadAllText(twin.TextPath), Is.EqualTo(Enemy.Replace("mass=\"2\"", "mass=\"4\"").Replace("0 1 0", "0 9 0")));
        }

        [Test]
        public void AScene_WrittenBeforeThePrefabItPlaces_IsBuiltOnceThePrefabExists()
        {
            textPath = $"{Folder}/Arena.uscene";
            scenePath = $"{Folder}/Arena.unity";
            File.WriteAllText(textPath, Arena);
            var status = Twins.Sync();
            Assert.That(status, Does.Contain("Arena.uscene:2: no prefab at Assets/ScenocoSyncTests/Enemy.prefab"));
            Assert.That(File.Exists(scenePath), Is.False, "no half-made scene is left behind");

            PrefabFromText("Enemy", Enemy);                 // the prefab arrives; nobody touches the scene's file again
            Assert.That(Twins.Find(textPath).Status, Is.EqualTo(TwinStatus.Synced), Twins.Status());
            Assert.That(Get(OpenScene(), "Grunt"), Is.Not.Null);
        }

        // --- prefabs inside prefabs --------------------------------------------------------------------

        [Test]
        public void APrefabPlacedInsideAPrefab_IsWrittenAsAPlacement()
        {
            PrefabFromText("Enemy", Enemy);
            const string squad = @"<Prefab name=""Squad"">
  <PrefabInstance prefab=""Assets/ScenocoSyncTests/Enemy.prefab"" name=""Leader"">
    <Transform localPosition=""0 0 2"" />
    <Rigidbody2D mass=""9"" />
  </PrefabInstance>
  <PrefabInstance prefab=""Assets/ScenocoSyncTests/Enemy.prefab"" name=""Enemy"" />
</Prefab>
";
            var twin = PrefabFromText("Squad", squad);
            var asset = Asset(Folder + "/Squad.prefab");
            var leader = asset.transform.Find("Leader").gameObject;
            Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(leader), Is.EqualTo(Asset()));
            Assert.That(leader.GetComponent<Rigidbody2D>().mass, Is.EqualTo(9));
            Assert.That(asset.transform.Find("Enemy/Muzzle"), Is.Not.Null);

            twin.Forget();
            twin.Poll(true);
            Assert.That(twin.Message, Is.EqualTo("in step"), "read back, it says what the file says");
        }

        // --- prefabs placed in a scene ----------------------------------------------------------------

        const string Arena = @"<Scene name=""Arena"">
  <PrefabInstance prefab=""Assets/ScenocoSyncTests/Enemy.prefab"" name=""Boss"">
    <Transform localPosition=""4 0 0"" />
    <Rigidbody2D mass=""5"" />
    <GameObject name=""Muzzle"">
      <Transform localPosition=""0 2 0"" />
    </GameObject>
    <GameObject name=""Flag"" />
  </PrefabInstance>
  <PrefabInstance prefab=""Assets/ScenocoSyncTests/Enemy.prefab"" name=""Grunt"">
    <Transform localPosition=""-4 0 0"" />
  </PrefabInstance>
</Scene>
";

        [Test]
        public void PlacedPrefabs_AreInstances_AndTheirTextIsOnlyOverrides()
        {
            PrefabFromText("Enemy", Enemy);
            var twin = FromText("Arena", Arena);
            var scene = OpenScene();
            var boss = Get(scene, "Boss");
            var grunt = Get(scene, "Grunt");

            Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(boss), Is.EqualTo(Asset()), "a real placement of the prefab");
            Assert.That(boss.GetComponent<Rigidbody2D>().mass, Is.EqualTo(5), "overridden");
            Assert.That(boss.transform.Find("Muzzle").localPosition, Is.EqualTo(new Vector3(0, 2, 0)), "the prefab's own child, overridden");
            Assert.That(boss.GetComponentsInChildren<Transform>(true).Count(t => t.name == "Muzzle"), Is.EqualTo(1), "not a second Muzzle");
            Assert.That(PrefabUtility.IsAddedGameObjectOverride(boss.transform.Find("Flag").gameObject), Is.True, "a child the prefab doesn't have");
            Assert.That(grunt.GetComponent<Rigidbody2D>().mass, Is.EqualTo(2), "the prefab's value");
            Assert.That(grunt.transform.localPosition, Is.EqualTo(new Vector3(-4, 0, 0)));

            // The scene read back says exactly what the file says: starting over from the scene finds no difference.
            twin.Forget();
            twin.Poll(true);
            Assert.That(twin.Message, Is.EqualTo("in step"));
        }

        [Test]
        public void AChangedPrefab_ReachesPlacementsThatDontOverrideIt()
        {
            var enemy = PrefabFromText("Enemy", Enemy);
            FromText("Arena", Arena);
            File.WriteAllText(enemy.TextPath, Enemy.Replace("mass=\"2\"", "mass=\"7\""));
            Twins.Sync();
            var scene = OpenScene();
            Assert.That(Get(scene, "Grunt").GetComponent<Rigidbody2D>().mass, Is.EqualTo(7));
            Assert.That(Get(scene, "Boss").GetComponent<Rigidbody2D>().mass, Is.EqualTo(5), "its override stands");
        }

        [Test]
        public void AnOverrideRemovedFromTheText_GoesBackToThePrefabsValue()
        {
            PrefabFromText("Enemy", Enemy);
            FromText("Arena", Arena);
            File.WriteAllText(textPath, Arena.Replace("    <Rigidbody2D mass=\"5\" />\n", "").Replace("    <GameObject name=\"Muzzle\">\n      <Transform localPosition=\"0 2 0\" />\n    </GameObject>\n", ""));
            Twins.Sync();
            var scene = OpenScene();
            var boss = Get(scene, "Boss");
            Assert.That(boss.GetComponent<Rigidbody2D>(), Is.Not.Null, "the prefab's component is reverted, never removed");
            Assert.That(boss.GetComponent<Rigidbody2D>().mass, Is.EqualTo(2));
            Assert.That(boss.transform.Find("Muzzle").localPosition, Is.EqualTo(new Vector3(0, 1, 0)), "the prefab's child is reverted, never removed");
            Assert.That(boss.transform.Find("Flag"), Is.Not.Null);
        }

        [Test]
        public void AnOverrideMadeInUnity_IsWrittenIntoThePlacement()
        {
            PrefabFromText("Enemy", Enemy);
            FromText("Arena", Arena);
            var scene = OpenScene();
            var grunt = Get(scene, "Grunt");
            Edit(grunt.transform.Find("Muzzle"), "m_LocalPosition", p => p.vector3Value = new Vector3(0, 3, 0));
            Edit(grunt.GetComponent<Rigidbody2D>(), "m_GravityScale", p => p.floatValue = 0);
            Save(scene);

            Assert.That(Text, Is.EqualTo(Arena.Replace(
                "  <PrefabInstance prefab=\"Assets/ScenocoSyncTests/Enemy.prefab\" name=\"Grunt\">\n    <Transform localPosition=\"-4 0 0\" />\n  </PrefabInstance>\n",
                "  <PrefabInstance prefab=\"Assets/ScenocoSyncTests/Enemy.prefab\" name=\"Grunt\">\n    <Transform localPosition=\"-4 0 0\" />\n" +
                "    <Rigidbody2D gravityScale=\"0\" />\n" +
                "    <GameObject name=\"Muzzle\">\n      <Transform localPosition=\"0 3 0\" />\n    </GameObject>\n" +
                "  </PrefabInstance>\n")));
        }
    
        // --- what a placement doesn't have of its prefab ------------------------------------------------

        const string Gate = @"<Scene name=""Gate"">
  <PrefabInstance prefab=""Assets/ScenocoSyncTests/Enemy.prefab"" name=""Ghost"">
    <!-- no body, no gun -->
    <PrefabInstance.removedComponents>
      <Rigidbody2D />
    </PrefabInstance.removedComponents>
    <PrefabInstance.removedGameObjects>
      <GameObject name=""Muzzle"" />
    </PrefabInstance.removedGameObjects>
  </PrefabInstance>
</Scene>
";

        [Test]
        public void RemovedComponentsAndChildren_AreRemovedFromThePlacement()
        {
            PrefabFromText("Enemy", Enemy);
            var twin = FromText("Gate", Gate);
            var ghost = Get(OpenScene(), "Ghost");
            Assert.That(ghost.GetComponent<Rigidbody2D>(), Is.Null);
            Assert.That(ghost.transform.Find("Muzzle"), Is.Null);
            Assert.That(PrefabUtility.GetRemovedComponents(ghost).Count, Is.EqualTo(1), "a removed-component override, as the Inspector makes");
            Assert.That(PrefabUtility.GetRemovedGameObjects(ghost).Count, Is.EqualTo(1));

            twin.Forget();
            twin.Poll(true);
            Assert.That(twin.Message, Is.EqualTo("in step"), "read back, it says what the file says");
        }

        [Test]
        public void AComponentOrChildRemovedInUnity_IsListedInThePlacement()
        {
            PrefabFromText("Enemy", Enemy);
            FromText("Arena", Arena);
            var scene = OpenScene();
            var grunt = Get(scene, "Grunt");
            Undo.DestroyObjectImmediate(grunt.GetComponent<Rigidbody2D>());
            Undo.DestroyObjectImmediate(grunt.transform.Find("Muzzle").gameObject);
            Save(scene);

            Assert.That(Text, Is.EqualTo(Arena.Replace(
                "  <PrefabInstance prefab=\"Assets/ScenocoSyncTests/Enemy.prefab\" name=\"Grunt\">\n    <Transform localPosition=\"-4 0 0\" />\n  </PrefabInstance>\n",
                "  <PrefabInstance prefab=\"Assets/ScenocoSyncTests/Enemy.prefab\" name=\"Grunt\">\n" +
                "    <PrefabInstance.removedComponents>\n" +
                "      <Rigidbody2D />\n" +
                "    </PrefabInstance.removedComponents>\n" +
                "    <PrefabInstance.removedGameObjects>\n" +
                "      <GameObject name=\"Muzzle\" />\n" +
                "    </PrefabInstance.removedGameObjects>\n" +
                "    <Transform localPosition=\"-4 0 0\" />\n" +
                "  </PrefabInstance>\n")), "the placement's own lists go ahead of its components");
        }

        [Test]
        public void ARemovalTakenOutOfTheText_BringsThePrefabsComponentBack()
        {
            PrefabFromText("Enemy", Enemy);
            FromText("Gate", Gate);
            File.WriteAllText(textPath, Gate.Replace("    <PrefabInstance.removedComponents>\n      <Rigidbody2D />\n    </PrefabInstance.removedComponents>\n", "")
                .Replace("    <PrefabInstance.removedGameObjects>\n      <GameObject name=\"Muzzle\" />\n    </PrefabInstance.removedGameObjects>\n", "    <GameObject name=\"Muzzle\">\n      <Transform localPosition=\"0 3 0\" />\n    </GameObject>\n"));
            Twins.Sync();
            Assert.That(Twins.Find(textPath).Status, Is.EqualTo(TwinStatus.Synced), Twins.Status());

            var ghost = Get(OpenScene(), "Ghost");
            Assert.That(ghost.GetComponent<Rigidbody2D>().mass, Is.EqualTo(2), "back, with the prefab's values");
            Assert.That(ghost.transform.Find("Muzzle").localPosition, Is.EqualTo(new Vector3(0, 3, 0)), "back, and overridden in the same edit");
            Assert.That(PrefabUtility.GetRemovedComponents(ghost), Is.Empty);
            Assert.That(PrefabUtility.GetRemovedGameObjects(ghost), Is.Empty);
        }

        [Test]
        public void ARemovalOfSomethingThePrefabDoesntHave_IsAnErrorAtItsLine()
        {
            PrefabFromText("Enemy", Enemy);
            textPath = $"{Folder}/Gate.uscene";
            File.WriteAllText(textPath, Gate.Replace("<Rigidbody2D />", "<BoxCollider2D />"));
            Twins.Sync();
            Assert.That(Twins.Find(textPath).Status, Is.EqualTo(TwinStatus.Invalid));
            Assert.That(Twins.Find(textPath).Message, Does.Contain("Gate.uscene:5: the prefab's \"Enemy\" has 0 BoxCollider2D"));
        }

        [Test]
        public void OnlyTheSecondOfTwoComponentsOverridden_IsStillTheSecondInTheText()
        {
            PrefabFromText("Twins", "<Prefab name=\"Twins\">\n  <BoxCollider2D />\n  <BoxCollider2D />\n</Prefab>\n");
            textPath = $"{Folder}/Pair.uscene";
            scenePath = $"{Folder}/Pair.unity";
            const string pair = "<Scene name=\"Pair\">\n  <PrefabInstance prefab=\"Assets/ScenocoSyncTests/Twins.prefab\" name=\"Twins\" />\n</Scene>\n";
            var twin = FromText("Pair", pair);
            var scene = OpenScene();
            Edit(Get(scene, "Twins").GetComponents<BoxCollider2D>()[1], "m_IsTrigger", p => p.boolValue = true);
            Save(scene);

            Assert.That(Text, Is.EqualTo(pair.Replace(" />\n</Scene>", ">\n    <BoxCollider2D />\n    <BoxCollider2D isTrigger=\"true\" />\n  </PrefabInstance>\n</Scene>")),
                "the first is written, empty, so the override stays on the second");
            twin.Forget();
            twin.Poll(true);
            Assert.That(twin.Message, Is.EqualTo("in step"));
        }

        // --- prefab variants -------------------------------------------------------------------------------

        const string Boss = @"<Prefab base=""Assets/ScenocoSyncTests/Enemy.prefab"" name=""Boss"">
  <Rigidbody2D mass=""9"" />
  <GameObject name=""Muzzle"">
    <Transform localPosition=""0 5 0"" />
  </GameObject>
  <GameObject name=""Crown"" />
</Prefab>
";

        const string BossPath = Folder + "/Boss.prefab";

        [Test]
        public void AVariant_IsCreatedFromItsText()
        {
            PrefabFromText("Enemy", Enemy);
            var twin = PrefabFromText("Boss", Boss);
            var boss = Asset(BossPath);
            Assert.That(PrefabUtility.GetPrefabAssetType(boss), Is.EqualTo(PrefabAssetType.Variant));
            Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(boss), Is.EqualTo(Asset()), "based on Enemy");
            Assert.That(boss.GetComponent<Rigidbody2D>().mass, Is.EqualTo(9));
            Assert.That(boss.transform.Find("Muzzle").localPosition, Is.EqualTo(new Vector3(0, 5, 0)));
            Assert.That(boss.GetComponentsInChildren<Transform>(true).Count(t => t.name == "Muzzle"), Is.EqualTo(1), "the base's child, not a second one");
            Assert.That(boss.transform.Find("Crown"), Is.Not.Null);

            twin.Forget();
            twin.Poll(true);
            Assert.That(twin.Message, Is.EqualTo("in step"), "read back, it says what the file says");
        }

        [Test]
        public void Import_BuildsAVariantWithItsRemovals_AndExportWritesItBack()
        {
            PrefabFromText("Enemy", Enemy);
            const string ghost = "<Prefab base=\"Assets/ScenocoSyncTests/Enemy.prefab\" name=\"Ghost\">\n" +
                                 "  <Prefab.removedComponents>\n    <Rigidbody2D />\n  </Prefab.removedComponents>\n" +
                                 "  <GameObject name=\"Muzzle\">\n    <Transform localPosition=\"0 7 0\" />\n  </GameObject>\n</Prefab>\n";
            var go = (GameObject)Files.Import(ghost, "Ghost.uprefab");
            try
            {
                Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(go), Is.EqualTo(Asset()));
                Assert.That(go.GetComponent<Rigidbody2D>(), Is.Null);
                Assert.That(go.transform.Find("Muzzle").localPosition, Is.EqualTo(new Vector3(0, 7, 0)));
                Assert.That(Files.Export(go), Is.EqualTo(ghost));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void AChangedBase_ReachesTheVariantWhereItDoesntOverride()
        {
            var enemy = PrefabFromText("Enemy", Enemy);
            PrefabFromText("Boss", Boss);
            File.WriteAllText(enemy.TextPath, Enemy.Replace("mass=\"2\"", "mass=\"3\" gravityScale=\"0\""));
            Twins.Sync();
            var boss = Asset(BossPath).GetComponent<Rigidbody2D>();
            Assert.That(boss.gravityScale, Is.EqualTo(0), "from the base");
            Assert.That(boss.mass, Is.EqualTo(9), "the variant's override stands");
        }

        [Test]
        public void AVariantMadeInUnity_IsTrackedAsOverridesOfItsBase()
        {
            PrefabFromText("Enemy", Enemy);
            var placed = PrefabUtility.InstantiatePrefab(Asset(), Scenoco.BuildScene.Get()) as GameObject;
            Edit(placed.transform.Find("Muzzle"), "m_LocalPosition", p => p.vector3Value = new Vector3(0, 5, 0));
            Object.DestroyImmediate(placed.GetComponent<Rigidbody2D>());
            PrefabUtility.SaveAsPrefabAsset(placed, BossPath);
            Object.DestroyImmediate(placed);

            Files.Track(BossPath);
            Assert.That(File.ReadAllText(Folder + "/Boss.uprefab"), Is.EqualTo(
                "<Prefab base=\"Assets/ScenocoSyncTests/Enemy.prefab\" name=\"Boss\">\n" +
                "  <Prefab.removedComponents>\n    <Rigidbody2D />\n  </Prefab.removedComponents>\n" +
                "  <GameObject name=\"Muzzle\">\n    <Transform localPosition=\"0 5 0\" />\n  </GameObject>\n</Prefab>\n"));
        }

        [Test]
        public void TakingTheBaseOutOfTheText_MakesAPrefabOfItsOwn()
        {
            PrefabFromText("Enemy", Enemy);
            var twin = PrefabFromText("Boss", Boss);
            File.WriteAllText(twin.TextPath, Boss.Replace(" base=\"Assets/ScenocoSyncTests/Enemy.prefab\"", ""));
            Twins.Sync();
            Assert.That(twin.Status, Is.EqualTo(TwinStatus.Synced), twin.Message);
            var boss = Asset(BossPath);
            Assert.That(PrefabUtility.GetPrefabAssetType(boss), Is.EqualTo(PrefabAssetType.Regular), "unpacked, as Unity's Unpack does");
            Assert.That(boss.GetComponent<Rigidbody2D>().mass, Is.EqualTo(9));
            Assert.That(boss.transform.Find("Muzzle").localPosition, Is.EqualTo(new Vector3(0, 5, 0)));
        }

        [Test]
        public void ANewBase_IsAnError_AndChangesNothing()
        {
            PrefabFromText("Enemy", Enemy);
            PrefabFromText("Elite", Enemy.Replace("\"Enemy\"", "\"Elite\""));
            var twin = PrefabFromText("Boss", Boss);
            File.WriteAllText(twin.TextPath, Boss.Replace("Enemy.prefab", "Elite.prefab").Replace("mass=\"9\"", "mass=\"8\""));
            Twins.Sync();
            Assert.That(twin.Status, Is.EqualTo(TwinStatus.Invalid));
            Assert.That(twin.Message, Does.Contain("Boss.uprefab:1: a variant's base can't be changed"));
            Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(Asset(BossPath)), Is.EqualTo(Asset()));
            Assert.That(Asset(BossPath).GetComponent<Rigidbody2D>().mass, Is.EqualTo(9), "nothing of the edit applied");
        }
}
}
