using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Scenoco.Tests
{
    /// <summary>
    /// uGUI (com.unity.ugui), when the project has it: this assembly compiles only then. Scenoco and
    /// Diceload don't need it; a game picks uGUI or UI Toolkit.
    /// </summary>
    public class UGUITests
    {
        readonly List<Object> made = new();

        GameObject Import(string text)
        {
            var o = (GameObject)Files.Import(text, "test");
            made.Add(o);
            return o;
        }

        [TearDown]
        public void Clean()
        {
            foreach (var o in made) if (o != null) Object.DestroyImmediate(o);
            made.Clear();
        }

        [Test]
        public void AnImageAndAButtonEvent_RoundTrip()
        {
            const string text = "<Prefab name=\"Menu\">\n" +
                                "  <GameObject name=\"Play\">\n" +
                                "    <RectTransform sizeDelta=\"120 40\" />\n" +
                                "    <CanvasRenderer />\n" +
                                "    <Image sprite=\"Resources/unity_builtin_extra#UISprite\" />\n" +
                                "    <Button targetGraphic=\"#Play\">\n      <Button.m_OnClick>\n        <Call target=\"#Menu\" method=\"GameObject.SetActive\" argument=\"false\" />\n      </Button.m_OnClick>\n    </Button>\n" +
                                "  </GameObject>\n</Prefab>\n";
            var menu = Import(text);
            var play = menu.GetComponentsInChildren<Transform>().Single(t => t.name == "Play");
            Assert.That(play.GetComponent<Image>().sprite.name, Is.EqualTo("UISprite"), "UGUI's own sprite, a built-in resource");
            var click = play.GetComponent<Button>().onClick;
            Assert.That(click.GetPersistentEventCount(), Is.EqualTo(1));
            Assert.That(click.GetPersistentMethodName(0), Is.EqualTo("SetActive"));
            Assert.That(play.GetComponent<Button>().targetGraphic, Is.EqualTo(play.GetComponent<Image>()), "Unity's Reset links them when the Button is added after the Image");
            Assert.That(Files.Export(menu), Is.EqualTo(text));
        }
    }
}
