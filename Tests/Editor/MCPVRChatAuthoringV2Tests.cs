using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityMCP.Editor
{
    /// <summary>
    /// Protocol v4 VRChat routes: VRCFury Toggle/Armature Link, outfit bone matching, play-mode emulator
    /// control and capture, extended audit checks, blendshapes, UdonSharp. Tests that need VRCFury or
    /// UdonSharp are ignored in projects without them; the rest build their fixtures in the scene.
    /// </summary>
    [TestFixture]
    public class MCPVRChatAuthoringV2Tests
    {
        private GameObject _avatar;
        private List<UnityEngine.Object> _cleanup;

        [SetUp]
        public void SetUp()
        {
            _cleanup = new List<UnityEngine.Object>();
            MCPVRChatAuthoringCommands.ResetTestOverrides();
            _avatar = new GameObject("MCPTestAvatar_V2");
            _cleanup.Add(_avatar);
        }

        [TearDown]
        public void TearDown()
        {
            MCPVRChatAuthoringCommands.ResetTestOverrides();
            foreach (var obj in _cleanup)
            {
                if (obj != null) UnityEngine.Object.DestroyImmediate(obj);
            }
            _cleanup.Clear();
        }

        // ─── Routes ───

        [Test]
        public void NewRoutes_AreRegistered()
        {
            foreach (var route in new[]
                     {
                         "vrc/avatar/vrcfury/toggle", "vrc/avatar/vrcfury/armature-link", "vrc/avatar/outfit/attach",
                         "vrc/avatar/blendshapes/list", "vrc/avatar/blendshapes/set", "vrc/avatar/playmode/status",
                         "vrc/avatar/playmode/set", "vrc/avatar/playmode/capture", "vrc/world/udonsharp/create",
                         "vrc/world/udonsharp/attach"
                     })
            {
                Assert.IsTrue(MCPBridgeServer.KnownRoutes.Contains(route), $"{route} must be registered");
            }
        }

        // ─── Blendshapes ───

        [Test]
        public void Blendshapes_List_DefaultsToFaceMeshAndFilters()
        {
            AddSkin(_avatar.transform, "Hair", "Short");
            AddSkin(_avatar.transform, "Body", "Smile", "Blink", "vrc.v_aa");

            var res = MCPVRChatBlendshapeCommands.List(Args(("avatarPath", _avatar.name))) as Dictionary<string, object>;
            Assert.IsNotNull(res);
            Assert.AreEqual("Body", res["meshPath"]);
            Assert.AreEqual(3, res["blendShapeCount"]);
            var shapes = (List<object>)res["blendShapes"];
            Assert.AreEqual("Smile", ((Dictionary<string, object>)shapes[0])["name"]);
            var others = (List<object>)res["otherMeshes"];
            Assert.AreEqual("Hair", ((Dictionary<string, object>)others[0])["path"]);

            var filtered = MCPVRChatBlendshapeCommands.List(Args(("avatarPath", _avatar.name), ("filter", "bli"))) as Dictionary<string, object>;
            Assert.AreEqual(1, filtered["matchingCount"]);
            Assert.AreEqual("Blink", ((Dictionary<string, object>)((List<object>)filtered["blendShapes"])[0])["name"]);
        }

        [Test]
        public void Blendshapes_Set_AppliesAndRefusesUnknownNamesWithoutChanging()
        {
            // "E" (a viseme-style name) must not be suggested for every typo that contains an e.
            var body = AddSkin(_avatar.transform, "Body", "Smile", "Blink", "E");

            var bad = MCPVRChatBlendshapeCommands.Set(Args(("avatarPath", _avatar.name), ("meshPath", "Body"),
                ("weights", new Dictionary<string, object> { { "Smile", 50 }, { "Smlie", 10 } }))) as Dictionary<string, object>;
            Assert.AreEqual(false, bad["success"]);
            StringAssert.Contains("did you mean Smile", bad["error"].ToString());
            Assert.AreEqual(0f, body.GetBlendShapeWeight(0), "Nothing may change when one name is wrong.");

            Undo.IncrementCurrentGroup();
            var ok = MCPVRChatBlendshapeCommands.Set(Args(("avatarPath", _avatar.name), ("meshPath", "Body"),
                ("weights", new Dictionary<string, object> { { "Smile", 75 } }))) as Dictionary<string, object>;
            Assert.AreEqual(true, ok["success"]);
            Assert.AreEqual(75f, body.GetBlendShapeWeight(0));

            Undo.PerformUndo();
            Assert.AreEqual(0f, body.GetBlendShapeWeight(0), "Blendshape edits must be undoable.");
        }

        [Test]
        public void FaceTracking_Coverage_DetectsTheStandardInUse()
        {
            var unified = MCPVRChatBlendshapeCommands.FaceTrackingCoverage(
                MCPVRChatBlendshapeCommands.UnifiedExpressions.Take(60).Concat(new[] { "Body_Slim" }));
            Assert.AreEqual("unifiedExpressions", unified["detectedStandard"]);
            Assert.AreEqual(60, ((Dictionary<string, object>)unified["unifiedExpressions"])["found"]);

            // Matching ignores case and punctuation: "EYE_BLINK_LEFT" is ARKit's eyeBlinkLeft.
            var arkit = MCPVRChatBlendshapeCommands.FaceTrackingCoverage(
                MCPVRChatBlendshapeCommands.ARKit.Select(n => n.ToUpperInvariant()));
            Assert.AreEqual("arkit", arkit["detectedStandard"]);
            Assert.AreEqual(1.0, ((Dictionary<string, object>)arkit["arkit"])["coverage"]);

            var none = MCPVRChatBlendshapeCommands.FaceTrackingCoverage(new[] { "Smile", "Blink" });
            Assert.AreEqual("none", none["detectedStandard"]);
        }

        // ─── Audit checks ───

        [Test]
        public void AnimationPaths_ReportsMissingObjectsComponentsAndBlendshapes()
        {
            AddSkin(_avatar.transform, "Jacket", "Zip");

            var good = Track(new AnimationClip { name = "JacketOn" });
            AnimationUtility.SetEditorCurve(good, EditorCurveBinding.FloatCurve("Jacket", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0f, 0f, 1f));
            AnimationUtility.SetEditorCurve(good, EditorCurveBinding.FloatCurve("Jacket", typeof(SkinnedMeshRenderer), "blendShape.Zip"), AnimationCurve.Constant(0f, 0f, 100f));
            // Animator-typed curves are parameters/muscles, never object paths.
            AnimationUtility.SetEditorCurve(good, EditorCurveBinding.FloatCurve("", typeof(Animator), "SomeAap"), AnimationCurve.Constant(0f, 0f, 1f));

            var broken = Track(new AnimationClip { name = "OldToggles" });
            AnimationUtility.SetEditorCurve(broken, EditorCurveBinding.FloatCurve("Coat", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0f, 0f, 1f));
            AnimationUtility.SetEditorCurve(broken, EditorCurveBinding.FloatCurve("Jacket", typeof(MeshRenderer), "m_Enabled"), AnimationCurve.Constant(0f, 0f, 1f));
            AnimationUtility.SetEditorCurve(broken, EditorCurveBinding.FloatCurve("Jacket", typeof(SkinnedMeshRenderer), "blendShape.Unzip"), AnimationCurve.Constant(0f, 0f, 100f));
            // d4rk Avatar Optimizer's dummy binding is deliberate, not broken.
            AnimationUtility.SetEditorCurve(good, EditorCurveBinding.FloatCurve("ThisHopefullyDoesntExist", typeof(GameObject), "m_IsActive"), AnimationCurve.Constant(0f, 0f, 0f));

            var result = MCPVRChatAuditChecks.CheckClipBindings(_avatar, new List<(string, AnimationClip)> { ("FX", good), ("FX", broken) });
            Assert.AreEqual(2, result["clipsChecked"]);
            Assert.AreEqual(1, result["missingObjectCount"]);
            Assert.AreEqual(1, result["missingComponentCount"]);
            Assert.AreEqual(1, result["missingBlendShapeCount"]);
            Assert.AreEqual(3, result["brokenBindingCount"]);

            var issues = ((List<object>)result["issues"]).Cast<Dictionary<string, object>>().ToList();
            var missingObject = issues.Single(i => (string)i["problem"] == "missingObject");
            Assert.AreEqual("Coat", missingObject["path"]);
            CollectionAssert.AreEqual(new[] { "OldToggles" }, (List<string>)missingObject["clips"]);
            Assert.AreEqual("Unzip", issues.Single(i => (string)i["problem"] == "missingBlendShape")["blendShape"]);
        }

        [Test]
        public void MeshBounds_FlagsMeshesThatCullSeparately()
        {
            var body = AddSkin(_avatar.transform, "Body");
            body.localBounds = new Bounds(new Vector3(0f, 0.8f, 0f), new Vector3(1f, 1.6f, 1f));
            var hair = AddSkin(_avatar.transform, "Hair");
            hair.localBounds = new Bounds(new Vector3(0f, 1.5f, 0f), new Vector3(0.3f, 0.3f, 0.3f));

            var mismatched = MCPVRChatAuditChecks.AuditMeshBounds(_avatar);
            Assert.AreEqual(false, mismatched["consistent"]);
            Assert.AreEqual(1, mismatched["mismatchedCount"]);
            Assert.AreEqual("Hair", ((Dictionary<string, object>)((List<object>)mismatched["mismatched"])[0])["path"]);

            hair.localBounds = body.localBounds;
            Assert.AreEqual(true, MCPVRChatAuditChecks.AuditMeshBounds(_avatar)["consistent"]);
        }

        [Test]
        public void AnchorOverrides_FlagsRenderersLitFromDifferentPoints()
        {
            var chest = NewChild(_avatar.transform, "Chest");
            var body = AddSkin(_avatar.transform, "Body");
            var hair = AddSkin(_avatar.transform, "Hair");
            body.probeAnchor = chest;

            var split = MCPVRChatAuditChecks.AuditAnchorOverrides(_avatar);
            Assert.AreEqual(false, split["consistent"]);
            Assert.AreEqual(2, split["probePointCount"]);

            hair.probeAnchor = chest;
            var shared = MCPVRChatAuditChecks.AuditAnchorOverrides(_avatar);
            Assert.AreEqual(true, shared["consistent"]);
            StringAssert.Contains("Chest", shared["summary"].ToString());

            // Different anchor objects at one spot light alike (d4rk anchors each mesh to itself at the root).
            body.probeAnchor = body.transform;
            hair.probeAnchor = hair.transform;
            Assert.AreEqual(true, MCPVRChatAuditChecks.AuditAnchorOverrides(_avatar)["consistent"]);
        }

        [Test]
        public void Audit_IncludesTheNewChecks()
        {
            AddSkin(_avatar.transform, "Body", "Smile");
            var res = MCPVRChatAvatarCommands.GetAuditSync(Args(("avatarName", _avatar.name))) as Dictionary<string, object>;
            Assert.IsNotNull(res);
            Assert.IsTrue(res.ContainsKey("animationPaths"));
            Assert.IsTrue(res.ContainsKey("meshBounds"));
            Assert.IsTrue(res.ContainsKey("anchorOverrides"));
        }

        // ─── Outfit bone matching ───

        [Test]
        public void VRCFuryLinkReport_MatchesByNameAndListsExtraBones()
        {
            var avatarHips = BuildChain(_avatar.transform, "Armature", "Hips", "Spine", "Chest");
            var outfit = NewChild(_avatar.transform, "Hoodie");
            var outfitHips = BuildChain(outfit, "Armature", "Hips", "Spine", "Chest");
            NewChild(outfitHips, "Hood_Root");

            var report = MCPVRChatOutfitCommands.BuildVRCFuryLinkReport(_avatar.transform, outfitHips, avatarHips, "", true);
            Assert.AreEqual(3, report["matchedCount"]);
            Assert.AreEqual(1, report["unmatchedCount"]);
            var unmatched = (Dictionary<string, object>)((List<object>)report["unmatched"])[0];
            Assert.AreEqual("Hoodie/Armature/Hips/Hood_Root", unmatched["path"]);
        }

        [Test]
        public void VRCFuryLinkReport_PredictsTheSuffixFromTheRootBoneName()
        {
            var avatarHips = BuildChain(_avatar.transform, "Armature", "Hips", "Spine");
            var outfit = NewChild(_avatar.transform, "Hoodie");
            var outfitHips = BuildChain(outfit, "Armature", "Hips_Hoodie", "Spine_Hoodie");

            var report = MCPVRChatOutfitCommands.BuildVRCFuryLinkReport(_avatar.transform, outfitHips, avatarHips, "", true);
            Assert.AreEqual("_Hoodie", report["effectiveSuffix"]);
            Assert.AreEqual(true, report["suffixPredicted"]);
            Assert.AreEqual(2, report["matchedCount"]);
            Assert.AreEqual(0, report["unmatchedCount"]);
        }

        [Test]
        public void MatchReport_SuggestsTheAffixUnmatchedBonesShare()
        {
            var avatarHips = BuildChain(_avatar.transform, "Armature", "Hips");
            NewChild(avatarHips, "Spine");
            NewChild(avatarHips, "UpperLeg.L");
            var outfit = NewChild(_avatar.transform, "Hoodie");
            var outfitHips = BuildChain(outfit, "Armature", "Hips");
            NewChild(outfitHips, "Hoodie_Spine");
            NewChild(outfitHips, "Hoodie_UpperLeg.L");

            var report = MCPVRChatOutfitCommands.BuildVRCFuryLinkReport(_avatar.transform, outfitHips, avatarHips, "", true);
            Assert.AreEqual(2, report["unmatchedCount"]);
            Assert.AreEqual(2, ((List<string>)report["likelyNameMismatches"]).Count);
            StringAssert.Contains("prefix 'Hoodie_'", string.Join(" ", (List<string>)report["suggestions"]));
        }

        [Test]
        public void FindOutfitHips_UsesTheAvatarsHipsPathThenArmatureNaming()
        {
            var avatarHips = BuildChain(_avatar.transform, "Armature", "Hips");

            var dress = NewChild(_avatar.transform, "Dress");
            var dressHips = BuildChain(dress, "Armature", "Hips");
            Assert.AreEqual(dressHips, MCPVRChatOutfitCommands.FindOutfitHips(dress, avatarHips, _avatar.transform));

            var skirt = NewChild(_avatar.transform, "Skirt");
            var skirtHips = BuildChain(skirt, "Skeleton", "Hip_Skirt");
            Assert.AreEqual(skirtHips, MCPVRChatOutfitCommands.FindOutfitHips(skirt, avatarHips, _avatar.transform));
        }

        [Test]
        public void ExternalSkinReference_DistinguishesClothingFromRigidProps()
        {
            var dress = NewChild(_avatar.transform, "Dress");
            var dressHips = BuildChain(dress, "Armature", "Hips");
            var dressMesh = AddSkin(dress, "DressMesh");
            dressMesh.bones = new[] { dressHips };
            Assert.IsTrue(MCPVRChatOutfitCommands.HasExternalSkinBoneReference(dressHips, _avatar.transform));

            var hat = NewChild(_avatar.transform, "Hat");
            var hatBone = NewChild(hat, "HatBone");
            AddSkin(hatBone, "HatMesh").bones = new[] { hatBone };
            Assert.IsFalse(MCPVRChatOutfitCommands.HasExternalSkinBoneReference(hatBone, _avatar.transform));
        }

        [Test]
        public void OutfitAttach_RequiresAHumanoidAvatar()
        {
            var res = MCPVRChatOutfitCommands.Attach(Args(("avatarPath", _avatar.name), ("outfitPath", "Dress"))) as Dictionary<string, object>;
            Assert.AreEqual(false, res["success"]);
            StringAssert.Contains("humanoid Hips", res["error"].ToString());
        }

        // ─── VRCFury ───

        [Test]
        public void VRCFuryToggle_CreatesThenUpdatesTheFeatureByMenuPath()
        {
            var vfType = FindLoadedType("VF.Model.VRCFury");
            if (vfType == null) Assert.Ignore("VRCFury not present in this project.");
            MCPVRChatAuthoringCommands.TestVRCFuryInstalledOverride = true;

            NewChild(_avatar.transform, "Jacket");
            NewChild(_avatar.transform, "Hat");
            AddSkin(_avatar.transform, "Body", "Shrink");

            var created = MCPVRChatVRCFuryCommands.ConfigureToggle(Args(
                ("avatarPath", _avatar.name), ("menuPath", "Clothing/Jacket"),
                ("objects", new List<object> { "Jacket" }),
                ("blendShapes", new List<object> { new Dictionary<string, object> { { "name", "Shrink" }, { "value", 100 } } }),
                ("saved", true), ("defaultOn", true))) as Dictionary<string, object>;
            Assert.AreEqual(true, created["success"], created.ContainsKey("error") ? created["error"].ToString() : "");
            Assert.AreEqual("created", created["action"]);

            var comp = _avatar.GetComponent(vfType);
            Assert.IsNotNull(comp);
            var toggle = MCPVRChatUtil.GetFieldValue(comp, "content");
            Assert.AreEqual("Toggle", toggle.GetType().Name);
            Assert.AreEqual("Clothing/Jacket", MCPVRChatUtil.GetFieldValue(toggle, "name"));
            Assert.AreEqual(true, MCPVRChatUtil.GetFieldValue(toggle, "saved"));
            Assert.AreEqual(2, ActionCount(toggle));

            var updated = MCPVRChatVRCFuryCommands.ConfigureToggle(Args(
                ("avatarPath", _avatar.name), ("menuPath", "Clothing/Jacket"),
                ("objects", new List<object> { new Dictionary<string, object> { { "path", "Hat" }, { "mode", "off" } } }))) as Dictionary<string, object>;
            Assert.AreEqual("updated", updated["action"]);
            Assert.AreEqual(1, _avatar.GetComponents(vfType).Length, "Updating must not add a second component.");
            Assert.AreEqual(1, ActionCount(MCPVRChatUtil.GetFieldValue(comp, "content")));

            var bad = MCPVRChatVRCFuryCommands.ConfigureToggle(Args(
                ("avatarPath", _avatar.name), ("menuPath", "Props/Glasses"),
                ("objects", new List<object> { "Glases" }))) as Dictionary<string, object>;
            Assert.AreEqual(false, bad["success"]);
            StringAssert.Contains("'Glases' was not found", bad["error"].ToString());
            Assert.AreEqual(1, _avatar.GetComponents(vfType).Length, "A failed call must add nothing.");

            // Clothing/Jacket now turns Hat off; turning it on elsewhere breaks VRCFury's build, so it is refused.
            var conflict = MCPVRChatVRCFuryCommands.ConfigureToggle(Args(
                ("avatarPath", _avatar.name), ("menuPath", "Props/Hat"),
                ("objects", new List<object> { "Hat" }))) as Dictionary<string, object>;
            Assert.AreEqual(false, conflict["success"]);
            StringAssert.Contains("resting state", conflict["error"].ToString());
            Assert.AreEqual(1, _avatar.GetComponents(vfType).Length, "A refused conflict must add nothing.");
        }

        [Test]
        public void VRCFuryArmatureLink_LinksByPathAndUpdatesInPlace()
        {
            var vfType = FindLoadedType("VF.Model.VRCFury");
            if (vfType == null) Assert.Ignore("VRCFury not present in this project.");
            MCPVRChatAuthoringCommands.TestVRCFuryInstalledOverride = true;

            BuildChain(_avatar.transform, "Armature", "Hips", "Spine");
            var dress = NewChild(_avatar.transform, "Dress");
            var dressHips = BuildChain(dress, "Armature", "Hips", "Spine");
            NewChild(dressHips, "Bow");

            var args = Args(("avatarPath", _avatar.name), ("targetPath", "Dress"), ("propBonePath", "Armature/Hips"),
                ("linkTo", "Armature/Hips"), ("recursive", true));
            var created = MCPVRChatVRCFuryCommands.ConfigureArmatureLink(args) as Dictionary<string, object>;
            Assert.AreEqual(true, created["success"], created.ContainsKey("error") ? created["error"].ToString() : "");
            Assert.AreEqual("created", created["action"]);
            var match = (Dictionary<string, object>)created["boneMatch"];
            Assert.AreEqual(2, match["matchedCount"]);
            Assert.AreEqual(1, match["unmatchedCount"]);

            var again = MCPVRChatVRCFuryCommands.ConfigureArmatureLink(args) as Dictionary<string, object>;
            Assert.AreEqual("updated", again["action"]);
            Assert.AreEqual(1, dress.GetComponents(vfType).Length);
        }

        // ─── Play mode ───

        [Test]
        public void PlayMode_SetAndStatusExplainThatPlayModeIsNeeded()
        {
            var set = MCPVRChatPlayModeCommands.Set(Args(("parameters", new Dictionary<string, object> { { "Jacket", true } }))) as Dictionary<string, object>;
            Assert.AreEqual(false, set["success"]);
            Assert.AreEqual(true, set["notReady"]);
            StringAssert.Contains("Not in play mode", set["error"].ToString());

            var status = MCPVRChatPlayModeCommands.GetStatus(new Dictionary<string, object>()) as Dictionary<string, object>;
            Assert.AreEqual(false, status["isPlaying"]);
            Assert.AreEqual(false, status["ready"]);

            // Edit mode reports which emulator would take over, so callers can skip a pointless play-mode round trip.
            var inScene = status["emulatorsInScene"] as List<string>;
            Assert.IsNotNull(inScene);
            if (inScene.Count == 0) StringAssert.Contains("Tools/Gesture Manager Emulator", status["hint"].ToString());
        }

        [Test]
        public void PlayMode_CaptureRendersTheAvatarAsPng()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("No graphics device to render with.");
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.SetParent(_avatar.transform, false);

            var res = MCPVRChatPlayModeCommands.Capture(Args(("avatarPath", _avatar.name), ("width", 128), ("height", 96), ("view", "left"))) as Dictionary<string, object>;
            Assert.AreEqual(true, res["success"], res.ContainsKey("error") ? res["error"].ToString() : "");
            Assert.AreEqual(128, res["width"]);
            Assert.AreEqual(96, res["height"]);
            var png = Convert.FromBase64String((string)res["base64"]);
            CollectionAssert.AreEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
        }

        // ─── UdonSharp ───

        [Test]
        public void UdonSharp_CreateValidatesBeforeWritingAnything()
        {
            const string path = "Assets/__MCPTest_UdonSharp/Door.cs";
            var res = MCPVRChatUdonSharpCommands.Create(Args(("path", path), ("className", "Gate"))) as Dictionary<string, object>;
            Assert.AreEqual(false, res["success"]);
            if (FindLoadedType("UdonSharp.UdonSharpProgramAsset") == null)
                StringAssert.Contains("UdonSharp is not available", res["error"].ToString());
            else
                StringAssert.Contains("must match the file name", res["error"].ToString());
            Assert.IsFalse(File.Exists(path), "Nothing may be written when validation fails.");
        }

        [Test]
        public void UdonSharp_CreateRefusesClassesThatAreNotUdonSharpBehaviours()
        {
            if (FindLoadedType("UdonSharp.UdonSharpProgramAsset") == null) Assert.Ignore("UdonSharp not present in this project.");
            const string path = "Assets/__MCPTest_UdonSharp/Door.cs";
            var res = MCPVRChatUdonSharpCommands.Create(Args(("path", path),
                ("content", "using UnityEngine;\npublic class Door : MonoBehaviour { }\n"))) as Dictionary<string, object>;
            Assert.AreEqual(false, res["success"]);
            StringAssert.Contains("must derive from UdonSharpBehaviour", res["error"].ToString());
            Assert.IsFalse(File.Exists(path));
        }

        [Test]
        public void UdonSharp_OverwriteWithoutContentKeepsTheScript()
        {
            if (FindLoadedType("UdonSharp.UdonSharpProgramAsset") == null) Assert.Ignore("UdonSharp not present in this project.");
            const string dir = "Assets/__MCPTest_UdonSharpKeep";
            const string path = dir + "/KeepMe.cs";
            const string source = "using UdonSharp;\n\npublic class KeepMe : UdonSharpBehaviour\n{\n    // hand-written\n}\n";
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, source);
            try
            {
                // overwrite:true is how a conflicting program asset is replaced; it must never rewrite the script.
                var res = MCPVRChatUdonSharpCommands.Create(Args(("path", path), ("overwrite", true))) as Dictionary<string, object>;
                Assert.AreEqual(true, res["success"], res.ContainsKey("error") ? res["error"].ToString() : "");
                Assert.AreEqual(true, res["scriptKept"]);
                Assert.AreEqual(source, File.ReadAllText(path));
            }
            finally
            {
                AssetDatabase.DeleteAsset(dir);
            }
        }

        [Test]
        public void UdonSharp_AttachAsksWhichBehaviour()
        {
            var res = MCPVRChatUdonSharpCommands.Attach(Args(("targetPath", _avatar.name))) as Dictionary<string, object>;
            Assert.AreEqual(false, res["success"]);
            if (FindLoadedType("UdonSharp.UdonSharpProgramAsset") == null)
                StringAssert.Contains("UdonSharp is not available", res["error"].ToString());
            else
                StringAssert.Contains("programAssetPath, scriptPath or className", res["error"].ToString());
        }

        // ─── Helpers ───

        private static Dictionary<string, object> Args(params (string key, object value)[] pairs)
        {
            var args = new Dictionary<string, object>();
            foreach (var (key, value) in pairs) args[key] = value;
            return args;
        }

        private T Track<T>(T obj) where T : UnityEngine.Object
        {
            _cleanup.Add(obj);
            return obj;
        }

        private static Transform NewChild(Transform parent, string name)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            return t;
        }

        /// <summary>parent/first/second/… — returns the SECOND element (the hips under an armature object).</summary>
        private static Transform BuildChain(Transform parent, params string[] names)
        {
            Transform current = parent;
            Transform second = null;
            for (int i = 0; i < names.Length; i++)
            {
                current = NewChild(current, names[i]);
                if (i == 1) second = current;
            }
            return second ?? current;
        }

        private SkinnedMeshRenderer AddSkin(Transform parent, string name, params string[] shapes)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var skin = go.AddComponent<SkinnedMeshRenderer>();
            var mesh = Track(new Mesh { name = name + "Mesh" });
            mesh.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right };
            mesh.triangles = new[] { 0, 1, 2 };
            var delta = new Vector3[3];
            foreach (var shape in shapes) mesh.AddBlendShapeFrame(shape, 100f, delta, delta, delta);
            mesh.RecalculateBounds();
            skin.sharedMesh = mesh;
            skin.localBounds = mesh.bounds;
            return skin;
        }

        private static int ActionCount(object toggle)
        {
            var actions = MCPVRChatUtil.GetFieldValue(MCPVRChatUtil.GetFieldValue(toggle, "state"), "actions") as System.Collections.IList;
            return actions?.Count ?? -1;
        }

        private static Type FindLoadedType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }
    }
}
