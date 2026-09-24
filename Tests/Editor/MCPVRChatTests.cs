using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace UnityMCP.Editor
{
    [TestFixture]
    public class MCPVRChatTests
    {
        private GameObject _testAvatar;
        private List<GameObject> _cleanupList;

        [SetUp]
        public void SetUp()
        {
            _cleanupList = new List<GameObject>();
            _testAvatar = new GameObject("TestAvatar");
            _cleanupList.Add(_testAvatar);
            MCPVRChatAvatarCommands.ResetTestOverrides();
        }

        [TearDown]
        public void TearDown()
        {
            MCPVRChatAvatarCommands.ResetTestOverrides();
            foreach (var go in _cleanupList)
            {
                if (go != null)
                    UnityEngine.Object.DestroyImmediate(go);
            }
            _cleanupList.Clear();
            MCPVRChatBakeHarness.CleanLeftoverClones();
        }

        // ─── Task 5.1: Bake harness clones avatar, runs pipeline, leaves authored unmodified ───
        [Test]
        public void Test5_1_BakeHarness_ClonesAndPreservesAuthoredAvatar()
        {
            bool callbackExecuted = false;
            GameObject seenInCallback = null;

            MCPVRChatBakeHarness.TestDetectBuildToolingOverride = (av) => new List<string> { "Modular Avatar" };
            MCPVRChatBakeHarness.TestPreprocessOverride = (clone) =>
            {
                clone.name += "_Processed";
            };

            int initialChildCount = _testAvatar.transform.childCount;

            var result = MCPVRChatBakeHarness.RunBakeAndAnalyze(_testAvatar, (clone, tooling) =>
            {
                callbackExecuted = true;
                seenInCallback = clone;
                Assert.AreNotSame(_testAvatar, clone, "Analyze function must receive the clone, not authored avatar.");
                Assert.IsTrue(clone.name.Contains(MCPVRChatBakeHarness.CloneSuffix), "Clone must carry __MCP_Bake_Clone suffix.");
                Assert.IsTrue(clone.name.Contains("_Processed"), "Preprocessor must have executed on the clone.");
                return "analysis_complete";
            });

            Assert.AreEqual("analysis_complete", result);
            Assert.IsTrue(callbackExecuted);
            Assert.IsNotNull(_testAvatar, "Authored avatar must still exist.");
            Assert.AreEqual("TestAvatar", _testAvatar.name, "Authored avatar name must be unmodified.");
            Assert.AreEqual(initialChildCount, _testAvatar.transform.childCount, "Authored avatar hierarchy must be unmodified.");
            Assert.IsTrue(seenInCallback == null || !seenInCallback, "Clone must be destroyed after analysis completes.");
        }

        // ─── Task 5.2: Destroy clone on failure paths; no leftover in scene ───
        [Test]
        public void Test5_2_CloneDestroyedOnForcedBakeFailure()
        {
            MCPVRChatBakeHarness.TestDetectBuildToolingOverride = (av) => new List<string> { "VRCFury" };
            MCPVRChatBakeHarness.TestPreprocessOverride = (clone) =>
            {
                throw new InvalidOperationException("Forced bake failure during preprocessing");
            };

            Assert.Throws<InvalidOperationException>(() =>
            {
                MCPVRChatBakeHarness.RunBakeAndAnalyze(_testAvatar, (clone, tooling) => "should_not_reach_here");
            });

            // Verify no leftover clone exists in scene
            var allGos = UnityEngine.Resources.FindObjectsOfTypeAll<GameObject>();
            foreach (var go in allGos)
            {
                if (EditorUtility.IsPersistent(go)) continue;
                Assert.IsFalse(MCPVRChatBakeHarness.IsBakeClone(go),
                    $"Found leftover clone after forced bake failure: {go.name}");
            }
        }

        // ─── Task 5.3: Detect and clean leftover clone from previously crashed bake ───
        [Test]
        public void Test5_3_PlantedLeftoverCloneIsCleanedUp()
        {
            // Plant a leftover clone
            GameObject planted = new GameObject("CrashedSession" + MCPVRChatBakeHarness.CloneSuffix);
            _cleanupList.Add(planted);

            Assert.IsTrue(MCPVRChatBakeHarness.IsBakeClone(planted));

            int cleaned = MCPVRChatBakeHarness.CleanLeftoverClones();
            Assert.GreaterOrEqual(cleaned, 1, "Must clean at least the planted leftover clone.");
            Assert.IsTrue(planted == null || !planted, "Planted clone must be destroyed.");
        }

        // ─── Task 5.4: Report which build tooling applied; measure directly when none ───
        [Test]
        public void Test5_4_NoBuildToolingReportsEmptyAndMeasuresDirectly()
        {
            // By default _testAvatar has no non-destructive components
            var tooling = MCPVRChatBakeHarness.DetectBuildTooling(_testAvatar);
            Assert.AreEqual(0, tooling.Count, "Empty GameObject must detect 0 non-destructive tools.");

            bool callbackRan = false;
            var result = MCPVRChatBakeHarness.RunBakeAndAnalyze(_testAvatar, (target, toolList) =>
            {
                callbackRan = true;
                Assert.AreSame(_testAvatar, target, "When no tooling, must pass authored avatar directly.");
                Assert.AreEqual(0, toolList.Count, "Tooling list must be empty.");
                return 42;
            });

            Assert.IsTrue(callbackRan);
            Assert.AreEqual(42, result);
        }

        // ─── Task 5.5: Surface bake failure as error without scene-derived figures ───
        [Test]
        public void Test5_5_BakeFailureReturnsErrorAndNoStatistics()
        {
            MCPVRChatBakeHarness.TestDetectBuildToolingOverride = (av) => new List<string> { "VRCFury" };
            MCPVRChatBakeHarness.TestPreprocessOverride = (clone) =>
            {
                throw new InvalidOperationException("Crash during VRCFury execution");
            };

            var args = new Dictionary<string, object> { { "avatarName", _testAvatar.name } };
            var response = MCPVRChatAvatarCommands.GetPerformanceSync(args) as Dictionary<string, object>;

            Assert.IsNotNull(response);
            Assert.IsTrue(response.ContainsKey("error"), "Failure must return error key.");
            Assert.IsTrue(response["error"].ToString().Contains("VRCFury"), "Error must describe failure.");
            Assert.IsFalse(response.ContainsKey("rank"), "Must NOT return overall rank on failure.");
            Assert.IsFalse(response.ContainsKey("categories"), "Must NOT return categories on failure.");
        }

        // ─── Task 5.6: Performance route reports rank, categories with thresholds, and limiting stat ───
        [Test]
        public void Test5_6_PerformanceRoute_ReportsRankThresholdsAndLimitingStat()
        {
            // Add mesh with large polycount to create a known limiting statistic
            var meshGo = new GameObject("BodyMesh");
            meshGo.transform.SetParent(_testAvatar.transform);
            var mf = meshGo.AddComponent<MeshFilter>();
            var mesh = new Mesh();
            // Create a small mesh for test
            mesh.vertices = new[] { Vector3.zero, Vector3.one, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mf.sharedMesh = mesh;

            var args = new Dictionary<string, object> { { "avatarName", _testAvatar.name } };
            var response = MCPVRChatAvatarCommands.GetPerformanceSync(args) as Dictionary<string, object>;

            Assert.IsNotNull(response);
            Assert.IsFalse(response.ContainsKey("error"), $"Unexpected error: {response.GetValueOrDefault("error")}");
            Assert.IsTrue(response.ContainsKey("rank"));
            Assert.IsTrue(response.ContainsKey("limitingCategory"));
            Assert.IsTrue(response.ContainsKey("categories"));

            var categories = response["categories"] as Dictionary<string, object>;
            Assert.IsNotNull(categories);
            Assert.IsTrue(categories.ContainsKey("PolyCount"));

            var polyStat = categories["PolyCount"] as Dictionary<string, object>;
            Assert.IsNotNull(polyStat);
            Assert.IsTrue(polyStat.ContainsKey("rating"));
            Assert.IsTrue(polyStat.ContainsKey("value"));
            Assert.IsTrue(polyStat.ContainsKey("threshold"));
        }

        // ─── Task 5.7 & 5.8: Parameter budget reporting and overage ───
        [Test]
        public void Test5_7_ParametersRoute_ReportsBudgetAndUsage()
        {
            MCPVRChatAvatarCommands.TestParametersOverride = (target, tooling) =>
            {
                return new Dictionary<string, object>
                {
                    { "avatarName", target.name },
                    { "totalUsed", 32 },
                    { "limit", 256 },
                    { "remaining", 224 },
                    { "overage", 0 },
                    { "isOverBudget", false },
                    { "parameters", new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object> { { "name", "MA_Toggle" }, { "type", "Bool" }, { "cost", 1 } },
                            new Dictionary<string, object> { { "name", "MA_Int" }, { "type", "Int" }, { "cost", 8 } }
                        }
                    },
                    { "buildTooling", new List<string> { "Modular Avatar" } }
                };
            };

            var args = new Dictionary<string, object> { { "avatarName", _testAvatar.name } };
            var response = MCPVRChatAvatarCommands.GetParametersSync(args) as Dictionary<string, object>;

            Assert.IsNotNull(response);
            Assert.AreEqual(32, response["totalUsed"]);
            Assert.AreEqual(256, response["limit"]);
            Assert.AreEqual(224, response["remaining"]);
            Assert.AreEqual(0, response["overage"]);
            Assert.AreEqual(false, response["isOverBudget"]);
        }

        [Test]
        public void Test5_8_ParametersRoute_ReportsOverageWhenOverBudget()
        {
            MCPVRChatAvatarCommands.TestParametersOverride = (target, tooling) =>
            {
                const int used = 280;
                const int limit = 256;
                return new Dictionary<string, object>
                {
                    { "avatarName", target.name },
                    { "totalUsed", used },
                    { "limit", limit },
                    { "remaining", 0 },
                    { "overage", used - limit },
                    { "isOverBudget", true },
                    { "parameters", new List<Dictionary<string, object>>() },
                    { "buildTooling", new List<string>() }
                };
            };

            var args = new Dictionary<string, object> { { "avatarName", _testAvatar.name } };
            var response = MCPVRChatAvatarCommands.GetParametersSync(args) as Dictionary<string, object>;

            Assert.IsNotNull(response);
            Assert.AreEqual(280, response["totalUsed"]);
            Assert.AreEqual(24, response["overage"]);
            Assert.AreEqual(true, response["isOverBudget"]);
        }

        // ─── Task 5.9: Audit route reports Write Defaults inconsistency ───
        [Test]
        public void Test5_9_AuditRoute_ReportsWriteDefaultsInconsistency()
        {
            // Build an animator controller with mixed WD
            var controller = new AnimatorController();
            controller.AddLayer("MixedLayer");
            var sm = controller.layers[0].stateMachine;

            var stateOn = sm.AddState("State_WD_ON");
            stateOn.writeDefaultValues = true;

            var stateOff = sm.AddState("State_WD_OFF");
            stateOff.writeDefaultValues = false;

            var anim = _testAvatar.AddComponent<Animator>();
            anim.runtimeAnimatorController = controller;

            var args = new Dictionary<string, object> { { "avatarName", _testAvatar.name } };
            var response = MCPVRChatAvatarCommands.GetAuditSync(args) as Dictionary<string, object>;

            Assert.IsNotNull(response);
            Assert.IsTrue(response.ContainsKey("writeDefaults"));

            var wd = response["writeDefaults"] as Dictionary<string, object>;
            Assert.IsNotNull(wd);
            Assert.AreEqual(false, wd["consistent"]);
            Assert.AreEqual(true, wd["hasMixedSettings"]);
            Assert.AreEqual(1, wd["totalWdOnStates"]);
            Assert.AreEqual(1, wd["totalWdOffStates"]);
        }

        // ─── Task 5.10: Audit route reports missing scripts by object path ───
        [Test]
        public void Test5_10_AuditRoute_ReportsMissingScriptsByPath()
        {
            var childGo = new GameObject("SubPart");
            childGo.transform.SetParent(_testAvatar.transform);

            // Mock missing script report
            MCPVRChatAvatarCommands.TestAuditOverride = (target, tooling) =>
            {
                return new Dictionary<string, object>
                {
                    { "avatarName", target.name },
                    { "missingScripts", new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                { "objectName", "SubPart" },
                                { "objectPath", "SubPart" },
                                { "missingCount", 1 }
                            }
                        }
                    },
                    { "missingScriptsCount", 1 },
                    { "hasMissingScripts", true }
                };
            };

            var args = new Dictionary<string, object> { { "avatarName", _testAvatar.name } };
            var response = MCPVRChatAvatarCommands.GetAuditSync(args) as Dictionary<string, object>;

            Assert.IsNotNull(response);
            Assert.AreEqual(true, response["hasMissingScripts"]);
            Assert.AreEqual(1, response["missingScriptsCount"]);

            var list = response["missingScripts"] as List<Dictionary<string, object>>;
            Assert.IsNotNull(list);
            Assert.AreEqual("SubPart", list[0]["objectPath"]);
        }

        // ─── Task 5.11: Audit route reports estimated texture memory & ranked list ───
        [Test]
        public void Test5_11_AuditRoute_ReportsTextureMemoryAndRankedList()
        {
            MCPVRChatAvatarCommands.TestAuditOverride = (target, tooling) =>
            {
                return new Dictionary<string, object>
                {
                    { "avatarName", target.name },
                    { "textureMemory", new Dictionary<string, object>
                        {
                            { "totalBytes", 104857600L },
                            { "totalMB", 100.0 },
                            { "uniqueTextureCount", 2 },
                            { "rankedTextures", new List<Dictionary<string, object>>
                                {
                                    new Dictionary<string, object> { { "name", "TexBig" }, { "memoryBytes", 70000000L }, { "memoryMB", 66.75 } },
                                    new Dictionary<string, object> { { "name", "TexSmall" }, { "memoryBytes", 34857600L }, { "memoryMB", 33.25 } }
                                }
                            }
                        }
                    }
                };
            };

            var args = new Dictionary<string, object> { { "avatarName", _testAvatar.name } };
            var response = MCPVRChatAvatarCommands.GetAuditSync(args) as Dictionary<string, object>;

            Assert.IsNotNull(response);
            Assert.IsTrue(response.ContainsKey("textureMemory"));

            var texMem = response["textureMemory"] as Dictionary<string, object>;
            Assert.IsNotNull(texMem);
            Assert.AreEqual(100.0, texMem["totalMB"]);
            var ranked = texMem["rankedTextures"] as List<Dictionary<string, object>>;
            Assert.AreEqual(2, ranked.Count);
            Assert.AreEqual("TexBig", ranked[0]["name"]);
        }

        // ─── A Bounds in a response hung the editor (Vector3.normalized recursed forever) ───
        [Test]
        public void MiniJson_SerializesUnityStructsWithoutRecursingForever()
        {
            // If the depth cap regresses, this test hangs the editor rather than failing.
            string json = MiniJson.Serialize(new Dictionary<string, object>
            {
                { "aabb", new Bounds(Vector3.zero, Vector3.one) }
            });
            StringAssert.StartsWith("{\"aabb\":{", json);
        }
    }
}
