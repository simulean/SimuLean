using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SimuLean.Unity.Editor.Tests
{
    /// <summary>
    /// Naval library, looks from labels: an item's visual gets its item when assigned (<see cref="Item.VisualAttached"/>
    /// → <see cref="ItemViews"/>); the block number shows over the item (<see cref="ItemLabelView"/>); a sub-block / block
    /// takes the model of its kind scaled to its length × breadth × depth, a block without its own from its sub-blocks
    /// (<see cref="ParametricBlockLook"/>); a label value picks a prefab (<see cref="LookByLabel"/>).
    /// </summary>
    public class ItemLooksTests
    {
        private const string Items = "Assets/SimuLean.Unity/Prefabs/Items/Shipbuilding/";
        private readonly List<Object> created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in created) if (o != null) Object.DestroyImmediate(o);
            created.Clear();
        }

        private GameObject Spawn(string prefab)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Items + prefab + ".prefab"));
            created.Add(go);
            return go;
        }

        private static Item SubBlock(string block, string kind, double length, double breadth, double depth)
        {
            var item = new Item(0);
            item.SetId("SubBlock", 1, 0);
            item.SetLabelValue("block", block);
            item.SetLabelText("kind", kind);
            item.SetLabelValue("length", length);
            item.SetLabelValue("breadth", breadth);
            item.SetLabelValue("depth", depth);
            return item;
        }

        private static Vector3 Size(GameObject look)
        {
            var b = new Bounds();
            bool any = false;
            foreach (var r in look.GetComponentsInChildren<Renderer>())
            {
                if (r.GetComponent<TextMesh>() != null) continue;
                if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
            }
            return b.size;
        }

        [Test]
        public void ASubBlockTakesTheModelOfItsKindAndItsSize()
        {
            var go = Spawn("SubBlock");
            var item = SubBlock("2", "curved", 10, 8, 4);
            ItemViews.Install();
            item.vItem = go;                                     // the event binds the views
            var look = go.GetComponent<ParametricBlockLook>().Look;
            Assert.IsNotNull(look, "the look is built when the item gets its visual");
            Assert.AreEqual("Look", look.name);
            var size = Size(look);
            Assert.AreEqual(10f, size.x, 0.6f, "length along x");
            Assert.AreEqual(8f, size.z, 0.6f, "breadth along z");
            Assert.That(size.y > 3f && size.y < 6f, $"depth about 4 m (the model's lugs on top), got {size.y}");
            Assert.AreEqual("B2", go.GetComponent<ItemLabelView>().Text);

            var flat = Spawn("SubBlock");
            ItemViews.Bind(SubBlock("3", "flat", 12, 16, 1.6), flat);
            StringAssert.Contains("Flat", flat.GetComponent<ParametricBlockLook>().Look.GetComponentInChildren<Renderer>().name);
        }

        [Test]
        public void ABlockWithoutItsOwnSizeTakesItsSubBlocks()
        {
            var go = Spawn("Block");
            var block = new Item(0);
            block.SetId("Block", 7, 0);
            block.SetLabelValue("block", "4");
            block.AddItem(SubBlock("4", "flat", 12, 16, 2));
            block.AddItem(SubBlock("4", "flat", 10, 14, 2));
            ItemViews.Bind(block, go);
            var size = Size(go.GetComponent<ParametricBlockLook>().Look);
            Assert.AreEqual(12f, size.x, 0.6f);
            Assert.AreEqual(16f, size.z, 0.6f);
            Assert.AreEqual("B4", go.GetComponent<ItemLabelView>().Text);
        }

        [Test]
        public void ALabelValuePicksTheLook()
        {
            var go = new GameObject("Item");
            created.Add(go);
            var byLabel = go.AddComponent<LookByLabel>();
            byLabel.label = "stage";
            byLabel.looks.Add(new LookByLabel.Entry { value = "kit", prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Items + "PlateKit.prefab") });
            byLabel.looks.Add(new LookByLabel.Entry { value = "sub", prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Items + "SubBlock.prefab") });
            var item = SubBlock("5", "flat", 12, 16, 1.6);
            item.SetLabelText("stage", "kit");
            ItemViews.Bind(item, go);
            Assert.IsNotNull(byLabel.Look.GetComponentInChildren<MeshRenderer>(), "the kit");
            Assert.IsNull(byLabel.Look.GetComponent<ParametricBlockLook>());
            item.SetLabelText("stage", "sub");
            ItemViews.Bind(item, go);
            Assert.IsNotNull(byLabel.Look.GetComponent<ParametricBlockLook>()?.Look, "the sub-block, its own look bound too");
        }

        /// <summary>A reaction's Set Item Look tints and scales the item's visual — not what it carries, not its
        /// label text — and a new visual of the item gets the look too.</summary>
        [Test]
        public void ASetLookTintsAndScalesTheVisualButNotWhatItCarries()
        {
            ItemViews.Install();
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            created.Add(go);
            go.transform.localScale = new Vector3(1f, 2f, 1f);
            var partGo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            partGo.transform.SetParent(go.transform, false);
            var text = new GameObject("Label").AddComponent<TextMesh>();
            text.transform.SetParent(go.transform, false);

            var part = new Item(0);
            part.SetId("Part", 2, 0);
            part.vItem = partGo;
            var item = new Item(0);
            item.SetId("Box", 1, 0);
            item.AddItem(part);
            item.vItem = go;
            Assert.IsNull(ItemLooks.TintOf(go.GetComponent<Renderer>()), "no look yet");

            item.SetLook(new ItemLook(new LookColor(1, 0, 0), 2));
            Assert.AreEqual(Color.red, ItemLooks.TintOf(go.GetComponent<Renderer>()), "the visual is tinted");
            Assert.AreEqual(new Vector3(2f, 4f, 2f), go.transform.localScale, "and scaled from its own scale");
            Assert.IsNull(ItemLooks.TintOf(partGo.GetComponent<Renderer>()), "a carried item keeps its look");
            Assert.IsNull(ItemLooks.TintOf(text.GetComponent<Renderer>()), "a label text keeps its colour");
            Assert.IsNull(item.GetLabelText("color"), "no label written");

            item.SetLook(new ItemLook(new LookColor(0, 0, 1)));
            Assert.AreEqual(Color.blue, ItemLooks.TintOf(go.GetComponent<Renderer>()), "a new colour");
            Assert.AreEqual(new Vector3(2f, 4f, 2f), go.transform.localScale, "the scale stays (not compounded)");

            var replaced = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            created.Add(replaced);
            item.vItem = replaced;   // a station's output look
            Assert.AreEqual(Color.blue, ItemLooks.TintOf(replaced.GetComponent<Renderer>()), "a new visual gets the look");
            Assert.AreEqual(new Vector3(2f, 2f, 2f), replaced.transform.localScale);
        }

        [Test]
        public void TheShipItemsAreInTheLibraryFolder()
        {
            foreach (var n in new[] { "SteelPlate", "ProfileBundle", "PlateKit", "SubBlock", "Block", "BlockOrder" })
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<GameObject>(Items + n + ".prefab"), n);
        }
    }
}
