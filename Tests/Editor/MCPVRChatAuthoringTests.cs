using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    [TestFixture]
    public class MCPVRChatAuthoringTests
    {
        private GameObject _avatarRoot;
        private List<UnityEngine.Object> _cleanupObjects;

        [SetUp]
        public void SetUp()
        {
            _cleanupObjects = new List<UnityEngine.Object>();
            MCPVRChatAuthoringCommands.ResetTestOverrides();

            _avatarRoot = new GameObject("TestAvatar_Authoring");
            _cleanupObjects.Add(_avatarRoot);
        }

        [TearDown]
        public void TearDown()
        {
            MCPVRChatAuthoringCommands.ResetTestOverrides();
            foreach (var obj in _cleanupObjects)
            {
                if (obj != null)
                    UnityEngine.Object.DestroyImmediate(obj);
            }
            _cleanupObjects.Clear();
        }

        // ─── Task 7.1: Avatar descriptor read reporting view position, lip sync, eye look, playable layers ───
        [Test]
        public void Test7_1_DescriptorRead_ReportsAllSections()
        {
            MCPVRChatAuthoringCommands.TestDescriptorGetOverride = (args) =>
            {
                return new Dictionary<string, object>
                {
                    { "avatarName", "TestAvatar_Authoring" },
                    { "viewPosition", new Dictionary<string, object> { { "x", 0f }, { "y", 1.5f }, { "z", 0.1f } } },
                    { "lipSync", new Dictionary<string, object>
                        {
                            { "mode", "VisemeBlendShape" },
                            { "visemeSkinnedMesh", "Body" },
                            { "visemeBlendShapes", new List<string>(MCPVRChatAuthoringCommands.StandardVisemes) }
                        }
                    },
                    { "eyeLook", new Dictionary<string, object> { { "enabled", true } } },
                    { "playableLayers", new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object> { { "type", "FX" }, { "isDefault", false }, { "animatorController", "Assets/FX.controller" } }
                        }
                    },
                    { "expressions", new Dictionary<string, object>
                        {
                            { "expressionsMenu", "Assets/Expressions/Menu.asset" },
                            { "expressionParameters", "Assets/Expressions/Params.asset" }
                        }
                    }
                };
            };

            var res = MCPVRChatAuthoringCommands.GetDescriptor(new Dictionary<string, object>
            {
                { "avatarPath", _avatarRoot.name }
            }) as Dictionary<string, object>;

            Assert.IsNotNull(res);
            Assert.AreEqual("TestAvatar_Authoring", res["avatarName"]);

            var vp = res["viewPosition"] as Dictionary<string, object>;
            Assert.IsNotNull(vp);
            Assert.AreEqual(1.5f, vp["y"]);

            var ls = res["lipSync"] as Dictionary<string, object>;
            Assert.IsNotNull(ls);
            Assert.AreEqual("VisemeBlendShape", ls["mode"]);

            var el = res["eyeLook"] as Dictionary<string, object>;
            Assert.IsNotNull(el);
            Assert.AreEqual(true, el["enabled"]);

            var pl = res["playableLayers"] as List<Dictionary<string, object>>;
            Assert.IsNotNull(pl);
            Assert.AreEqual(1, pl.Count);
            Assert.AreEqual("FX", pl[0]["type"]);
        }

        // ─── Task 7.2: Viseme assignment mapping blendshapes & reporting unmatched unmapped without guessing ───
        [Test]
        public void Test7_2_VisemeAssignment_UnmatchedReportedUnmappedWithoutGuessing()
        {
            // The route writes the mapping onto the descriptor, so it refuses an avatar without one.
            var descType = FindLoadedType("VRC.SDK3.Avatars.Components.VRCAvatarDescriptor");
            if (descType == null) Assert.Ignore("VRChat Avatars SDK not present in this project.");
            _avatarRoot.AddComponent(descType);

            // Setup mesh with 14 matching visemes, missing "ou"
            var meshGo = new GameObject("Body");
            meshGo.transform.SetParent(_avatarRoot.transform);
            var smr = meshGo.AddComponent<SkinnedMeshRenderer>();
            var mesh = new Mesh { name = "BodyMesh" };
            _cleanupObjects.Add(mesh);

            Vector3[] verts = new Vector3[] { Vector3.zero, Vector3.one, Vector3.up };
            Vector3[] deltaVerts = new Vector3[] { Vector3.zero, Vector3.zero, Vector3.zero };
            mesh.vertices = verts;

            // Add all standard visemes EXCEPT "ou"
            foreach (var v in MCPVRChatAuthoringCommands.StandardVisemes)
            {
                if (v.Equals("ou", StringComparison.OrdinalIgnoreCase)) continue; // missing!
                mesh.AddBlendShapeFrame("vrc.v_" + v.ToLowerInvariant(), 100f, deltaVerts, deltaVerts, deltaVerts);
            }
            smr.sharedMesh = mesh;

            var res = MCPVRChatAuthoringCommands.SetVisemes(new Dictionary<string, object>
            {
                { "avatarPath", _avatarRoot.name },
                { "meshPath", "Body" }
            }) as Dictionary<string, object>;

            Assert.IsNotNull(res);
            Assert.AreEqual(true, res["success"]);
            Assert.AreEqual(14, res["visemesMapped"]);
            Assert.AreEqual(1, res["visemesUnmapped"]);

            var unmapped = res["unmappedVisemes"] as List<string>;
            Assert.IsNotNull(unmapped);
            Assert.IsTrue(unmapped.Contains("ou"), "Missing 'ou' viseme must be reported in unmappedVisemes");

            var mappings = res["mappings"] as Dictionary<string, string>;
            Assert.IsNotNull(mappings);
            Assert.IsNull(mappings["ou"], "Unmatched viseme must NOT be guessed, must be null/unmapped");
            Assert.AreEqual("vrc.v_sil", mappings["sil"]);
            Assert.AreEqual("vrc.v_pp", mappings["PP"]);
        }

        // ─── Task 7.3: Playable layer assignment updates controller reference ───
        [Test]
        public void Test7_3_PlayableLayerAssignment_DescriptorReferencesController()
        {
            MCPVRChatAuthoringCommands.TestSetPlayableLayerOverride = (args) =>
            {
                string lt = args["layerType"].ToString();
                string cp = args.ContainsKey("controllerPath") ? args["controllerPath"]?.ToString() : null;
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "avatarName", "TestAvatar_Authoring" },
                    { "layerType", lt },
                    { "controllerPath", cp },
                    { "isDefault", false }
                };
            };

            var res = MCPVRChatAuthoringCommands.SetPlayableLayer(new Dictionary<string, object>
            {
                { "avatarPath", _avatarRoot.name },
                { "layerType", "FX" },
                { "controllerPath", "Assets/Animations/CustomFX.controller" }
            }) as Dictionary<string, object>;

            Assert.IsNotNull(res);
            Assert.AreEqual(true, res["success"]);
            Assert.AreEqual("FX", res["layerType"]);
            Assert.AreEqual("Assets/Animations/CustomFX.controller", res["controllerPath"]);
            Assert.AreEqual(false, res["isDefault"]);
        }

        // ─── Task 7.4: Expression parameter create and modify ───
        [Test]
        public void Test7_4_ExpressionParameter_AddedWithCostAndDefaults()
        {
            MCPVRChatAuthoringCommands.TestCreateParamOverride = (args) =>
            {
                string name = args["name"].ToString();
                string type = args["type"].ToString();
                int cost = type == "Bool" ? 1 : 8;
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "name", name },
                    { "type", type },
                    { "defaultValue", 1f },
                    { "saved", true },
                    { "synced", true },
                    { "cost", cost },
                    { "totalUsed", cost },
                    { "limit", 256 },
                    { "remaining", 256 - cost }
                };
            };

            var res = MCPVRChatAuthoringCommands.CreateParameter(new Dictionary<string, object>
            {
                { "name", "OutfitToggle" },
                { "type", "Bool" },
                { "defaultValue", 1f },
                { "synced", true }
            }) as Dictionary<string, object>;

            Assert.IsNotNull(res);
            Assert.AreEqual(true, res["success"]);
            Assert.AreEqual("OutfitToggle", res["name"]);
            Assert.AreEqual(1, res["cost"]);
            Assert.AreEqual(255, res["remaining"]);
        }

        // ─── Task 7.6: Expression menu add control ───
        [Test]
        public void Test7_6_ExpressionMenu_AddControl()
        {
            MCPVRChatAuthoringCommands.TestAddMenuControlOverride = (args) =>
            {
                string name = args["name"].ToString();
                string type = args.ContainsKey("type") ? args["type"].ToString() : "Button";
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "refused", false },
                    { "controlName", name },
                    { "type", type },
                    { "controlCount", 1 },
                    { "limit", 8 }
                };
            };

            var res = MCPVRChatAuthoringCommands.AddMenuControl(new Dictionary<string, object>
            {
                { "name", "HatsSubmenu" },
                { "type", "SubMenu" }
            }) as Dictionary<string, object>;

            Assert.IsNotNull(res);
            Assert.AreEqual(true, res["success"]);
            Assert.AreEqual(false, res["refused"]);
            Assert.AreEqual("HatsSubmenu", res["controlName"]);
            Assert.AreEqual(1, res["controlCount"]);
            Assert.AreEqual(8, res["limit"]);
        }

        // ─── Task 7.7: Refuse menu control addition exceeding per-menu control limit (8) ───
        [Test]
        public void Test7_7_ExpressionMenu_ControlLimitExceededRefused()
        {
            MCPVRChatAuthoringCommands.TestAddMenuControlOverride = (args) =>
            {
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "refused", true },
                    { "controlCount", 8 },
                    { "limit", 8 },
                    { "error", "Expression menu already contains the maximum number of controls (8). Addition refused. Menu asset was unchanged." }
                };
            };

            var res = MCPVRChatAuthoringCommands.AddMenuControl(new Dictionary<string, object>
            {
                { "name", "NinthControl" },
                { "type", "Toggle" }
            }) as Dictionary<string, object>;

            Assert.IsNotNull(res);
            Assert.AreEqual(false, res["success"]);
            Assert.AreEqual(true, res["refused"]);
            Assert.AreEqual(8, res["controlCount"]);
            Assert.AreEqual(8, res["limit"]);
            StringAssert.Contains("maximum number of controls (8)", res["error"].ToString());
            StringAssert.Contains("unchanged", res["error"].ToString());
        }

        // ─── Task 7.8: PhysBone add, configure, and report with affected transform count ───
        [Test]
        public void Test7_8_PhysBone_AddAndReportAffectedTransforms()
        {
            // Build a small bone hierarchy: Hips -> Spine -> Chest -> Neck -> Head -> Hair1 -> Hair2
            var hips = new GameObject("Hips");
            hips.transform.SetParent(_avatarRoot.transform);
            var spine = new GameObject("Spine");
            spine.transform.SetParent(hips.transform);
            var chest = new GameObject("Chest");
            chest.transform.SetParent(spine.transform);
            var neck = new GameObject("Neck");
            neck.transform.SetParent(chest.transform);
            var head = new GameObject("Head");
            head.transform.SetParent(neck.transform);
            var hair1 = new GameObject("Hair1");
            hair1.transform.SetParent(head.transform);
            var hair2 = new GameObject("Hair2");
            hair2.transform.SetParent(hair1.transform);

            MCPVRChatAuthoringCommands.TestListPhysBonesOverride = (args) =>
            {
                // Hair chain root Hair1 has 2 affected transforms (Hair1 and Hair2)
                return new Dictionary<string, object>
                {
                    { "avatarName", "TestAvatar_Authoring" },
                    { "physBoneCount", 1 },
                    { "physBones", new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                { "objectPath", "Hips/Spine/Chest/Neck/Head/Hair1" },
                                { "root", "Hips/Spine/Chest/Neck/Head/Hair1" },
                                { "affectedTransformCount", 2 },
                                { "parameters", new Dictionary<string, object>
                                    {
                                        { "pull", 0.2f },
                                        { "spring", 0.5f },
                                        { "damping", 0.1f },
                                        { "radius", 0.05f },
                                        { "allowGrabbing", true }
                                    }
                                }
                            }
                        }
                    }
                };
            };

            var res = MCPVRChatAuthoringCommands.ListPhysBones(new Dictionary<string, object>
            {
                { "avatarPath", _avatarRoot.name }
            }) as Dictionary<string, object>;

            Assert.IsNotNull(res);
            Assert.AreEqual(1, res["physBoneCount"]);

            var pbList = res["physBones"] as List<Dictionary<string, object>>;
            Assert.IsNotNull(pbList);
            Assert.AreEqual(2, pbList[0]["affectedTransformCount"]);
            Assert.AreEqual("Hips/Spine/Chest/Neck/Head/Hair1", pbList[0]["root"]);
        }

        // ─── Task 7.9: Contact sender and receiver configuration ───
        [Test]
        public void Test7_9_Contacts_SenderAndReceiver()
        {
            MCPVRChatAuthoringCommands.TestListContactsOverride = (args) =>
            {
                return new Dictionary<string, object>
                {
                    { "avatarName", "TestAvatar_Authoring" },
                    { "contactCount", 2 },
                    { "contacts", new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                { "type", "sender" },
                                { "objectPath", "Hand_R/Finger" },
                                { "radius", 0.02f },
                                { "collisionTags", new List<string> { "Hand", "Finger" } },
                                { "parameter", null }
                            },
                            new Dictionary<string, object>
                            {
                                { "type", "receiver" },
                                { "objectPath", "Head/Nose" },
                                { "radius", 0.03f },
                                { "collisionTags", new List<string> { "Finger" } },
                                { "parameter", "BoopTrigger" }
                            }
                        }
                    }
                };
            };

            var res = MCPVRChatAuthoringCommands.ListContacts(new Dictionary<string, object>
            {
                { "avatarPath", _avatarRoot.name }
            }) as Dictionary<string, object>;

            Assert.IsNotNull(res);
            Assert.AreEqual(2, res["contactCount"]);

            var contacts = res["contacts"] as List<Dictionary<string, object>>;
            Assert.IsNotNull(contacts);
            Assert.AreEqual("sender", contacts[0]["type"]);
            Assert.AreEqual("receiver", contacts[1]["type"]);
            Assert.AreEqual("BoopTrigger", contacts[1]["parameter"]);
        }

        // ─── Task 7.10: Modular Avatar and VRCFury absent package error ───
        [Test]
        public void Test7_10_ModularAvatarAndVRCFury_FailsWhenAbsent()
        {
            // Modular Avatar absent
            MCPVRChatAuthoringCommands.TestModularAvatarInstalledOverride = false;
            var maRes = MCPVRChatAuthoringCommands.AddModularAvatarComponent(new Dictionary<string, object>
            {
                { "avatarPath", _avatarRoot.name },
                { "componentType", "ModularAvatarMergeAnimator" }
            }) as Dictionary<string, object>;

            Assert.IsNotNull(maRes);
            Assert.AreEqual(false, maRes["success"]);
            StringAssert.Contains("Modular Avatar is not installed", maRes["error"].ToString());
            StringAssert.Contains("nadena.dev.modular-avatar", maRes["error"].ToString());

            // VRCFury absent
            MCPVRChatAuthoringCommands.TestVRCFuryInstalledOverride = false;
            var vfRes = MCPVRChatAuthoringCommands.AddVRCFuryComponent(new Dictionary<string, object>
            {
                { "avatarPath", _avatarRoot.name }
            }) as Dictionary<string, object>;

            Assert.IsNotNull(vfRes);
            Assert.AreEqual(false, vfRes["success"]);
            StringAssert.Contains("VRCFury is not installed", vfRes["error"].ToString());
            StringAssert.Contains("com.vrcfury.vrcfury", vfRes["error"].ToString());
        }

        // ─── Task 7.11: Non-destructive component reporting with role ───
        [Test]
        public void Test7_11_NonDestructive_ReportsObjectPathAndRole()
        {
            MCPVRChatAuthoringCommands.TestListNonDestructiveOverride = (args) =>
            {
                return new Dictionary<string, object>
                {
                    { "avatarName", "TestAvatar_Authoring" },
                    { "totalCount", 2 },
                    { "components", new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                { "objectPath", "Clothing/Shirt" },
                                { "tool", "Modular Avatar" },
                                { "componentType", "ModularAvatarMergeAnimator" },
                                { "role", "Merge Animator (merge into avatar FX/Gesture layer)" }
                            },
                            new Dictionary<string, object>
                            {
                                { "objectPath", "Props/Glasses" },
                                { "tool", "VRCFury" },
                                { "componentType", "VRCFury" },
                                { "role", "VRCFury Features: Toggle" }
                            }
                        }
                    }
                };
            };

            var res = MCPVRChatAuthoringCommands.ListNonDestructive(new Dictionary<string, object>
            {
                { "avatarPath", _avatarRoot.name }
            }) as Dictionary<string, object>;

            Assert.IsNotNull(res);
            Assert.AreEqual(2, res["totalCount"]);

            var comps = res["components"] as List<Dictionary<string, object>>;
            Assert.IsNotNull(comps);
            Assert.AreEqual("Modular Avatar", comps[0]["tool"]);
            Assert.AreEqual("VRCFury", comps[1]["tool"]);
            StringAssert.Contains("Merge Animator", comps[0]["role"].ToString());
            StringAssert.Contains("VRCFury", comps[1]["role"].ToString());
        }

        // ─── A typo'd path must error, never silently retarget the edit onto the avatar root ───
        [Test]
        public void TargetPath_NotFound_ErrorsAndAddsNothing()
        {
            if (FindLoadedType("VRC.SDK3.Dynamics.Contact.Components.VRCContactReceiver") == null)
                Assert.Ignore("VRChat SDK contacts not present in this project.");
            new GameObject("Head").transform.SetParent(_avatarRoot.transform);
            int before = _avatarRoot.GetComponents<Component>().Length;

            var res = MCPVRChatAuthoringCommands.AddContact(new Dictionary<string, object>
            {
                { "avatarPath", _avatarRoot.name },
                { "targetPath", "Haed" },
                { "type", "receiver" }
            }) as Dictionary<string, object>;

            StringAssert.Contains("'Haed' was not found", res["error"].ToString());
            Assert.AreEqual(before, _avatarRoot.GetComponents<Component>().Length, "Nothing may be added to the root.");
        }

        // ─── VRCFury: the requested feature is set, unknown features are refused, and the add is undoable ───
        [Test]
        public void VRCFuryAdd_SetsFeature_RefusesUnknown_AndUndoes()
        {
            var vfType = FindLoadedType("VF.Model.VRCFury");
            if (vfType == null) Assert.Ignore("VRCFury not present in this project.");
            MCPVRChatAuthoringCommands.TestVRCFuryInstalledOverride = true;

            var bad = MCPVRChatAuthoringCommands.AddVRCFuryComponent(new Dictionary<string, object>
            {
                { "avatarPath", _avatarRoot.name }, { "feature", "NotAFeature" }
            }) as Dictionary<string, object>;
            StringAssert.Contains("Valid features:", bad["error"].ToString());
            StringAssert.Contains("Toggle", bad["error"].ToString());
            Assert.IsNull(_avatarRoot.GetComponent(vfType));

            Undo.IncrementCurrentGroup();
            var ok = MCPVRChatAuthoringCommands.AddVRCFuryComponent(new Dictionary<string, object>
            {
                { "avatarPath", _avatarRoot.name }, { "feature", "Toggle" }
            }) as Dictionary<string, object>;
            Assert.AreEqual(true, ok["success"]);
            var comp = _avatarRoot.GetComponent(vfType);
            Assert.IsNotNull(comp);
            Assert.AreEqual("Toggle", new SerializedObject(comp).FindProperty("content").managedReferenceValue.GetType().Name);

            Undo.PerformUndo();
            Assert.IsNull(_avatarRoot.GetComponent(vfType), "Undo must remove the added component.");
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
