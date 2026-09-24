using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    [TestFixture]
    public class MCPVRChatWorldTests
    {
        private List<UnityEngine.Object> _cleanupObjects;
        private GameObject _worldRoot;

        [SetUp]
        public void SetUp()
        {
            _cleanupObjects = new List<UnityEngine.Object>();
            MCPVRChatWorldCommands.ResetTestOverrides();

            _worldRoot = new GameObject("TestWorld_Root");
            _cleanupObjects.Add(_worldRoot);
        }

        [TearDown]
        public void TearDown()
        {
            MCPVRChatWorldCommands.ResetTestOverrides();
            foreach (var obj in _cleanupObjects)
            {
                if (obj != null)
                    UnityEngine.Object.DestroyImmediate(obj);
            }
            _cleanupObjects.Clear();
        }

        // ─── Task 8.1: Scene descriptor read route reporting spawns, order, respawn height, camera ───
        [Test]
        public void Test8_1_DescriptorRead_ReportsAllFields()
        {
            var descGo = new GameObject("WorldDescriptor");
            descGo.transform.SetParent(_worldRoot.transform);
            var desc = descGo.AddComponent<MockVRCSceneDescriptor>();
            _cleanupObjects.Add(descGo);

            var spawn1 = new GameObject("Spawn_1");
            spawn1.transform.position = new Vector3(0, 1, 0);
            _cleanupObjects.Add(spawn1);

            var spawn2 = new GameObject("Spawn_2");
            spawn2.transform.position = new Vector3(5, 2, 5);
            _cleanupObjects.Add(spawn2);

            var cam = new GameObject("RefCamera");
            _cleanupObjects.Add(cam);

            desc.spawns = new Transform[] { spawn1.transform, spawn2.transform };
            desc.spawnOrder = MockVRCSceneDescriptor.SpawnOrder.Random;
            desc.respawnHeightY = -50f;
            desc.referenceCamera = cam;
            desc.forbidUserPortals = true;

            MCPVRChatWorldCommands.TestSceneDescriptorOverride = desc;

            var res = MCPVRChatWorldCommands.GetDescriptor(new Dictionary<string, object>()) as Dictionary<string, object>;
            Assert.IsNotNull(res);
            Assert.IsFalse(res.ContainsKey("error"), "Should not return error on configured world scene");

            var spawns = res["spawns"] as List<Dictionary<string, object>>;
            Assert.IsNotNull(spawns);
            Assert.AreEqual(2, spawns.Count);
            Assert.AreEqual("Spawn_1", spawns[0]["name"]);
            Assert.AreEqual("Spawn_2", spawns[1]["name"]);

            Assert.AreEqual("Random", res["spawnOrder"]);
            Assert.AreEqual(-50f, res["respawnHeightY"]);
            Assert.AreEqual(MCPVRChatWorldCommands.GetGameObjectPath(cam), res["referenceCamera"]);
            Assert.AreEqual(true, res["forbidUserPortals"]);
        }

        // ─── Task 8.2: Spawn point add and modify routes ───
        [Test]
        public void Test8_2_SpawnPointAddAndModify_ReferencesNewSpawn()
        {
            var descGo = new GameObject("WorldDescriptor_AddSpawn");
            descGo.transform.SetParent(_worldRoot.transform);
            var desc = descGo.AddComponent<MockVRCSceneDescriptor>();
            _cleanupObjects.Add(descGo);

            MCPVRChatWorldCommands.TestSceneDescriptorOverride = desc;

            // Add spawn point
            var addRes = MCPVRChatWorldCommands.AddSpawn(new Dictionary<string, object>
            {
                { "name", "NewPlayerSpawn" },
                { "position", new float[] { 10f, 0f, -5f } },
                { "rotation", new float[] { 0f, 90f, 0f } }
            }) as Dictionary<string, object>;

            Assert.IsNotNull(addRes);
            Assert.IsTrue((bool)addRes["success"]);
            Assert.AreEqual(1, addRes["spawnCount"]);
            _cleanupObjects.Add(desc.spawns[0].gameObject); // AddSpawn creates it in the open scene

            var spawnInfo = addRes["spawn"] as Dictionary<string, object>;
            Assert.IsNotNull(spawnInfo);
            Assert.AreEqual("NewPlayerSpawn", spawnInfo["name"]);

            // Verify descriptor references the new spawn
            Assert.AreEqual(1, desc.spawns.Length);
            Assert.AreEqual("NewPlayerSpawn", desc.spawns[0].name);
            Assert.AreEqual(new Vector3(10f, 0f, -5f), desc.spawns[0].position);

            // Modify descriptor spawns / settings
            var setRes = MCPVRChatWorldCommands.SetSpawns(new Dictionary<string, object>
            {
                { "spawnOrder", "Demographic" },
                { "respawnHeightY", -25f }
            }) as Dictionary<string, object>;

            Assert.IsNotNull(setRes);
            Assert.AreEqual("Demographic", setRes["spawnOrder"]);
            Assert.AreEqual(-25f, setRes["respawnHeightY"]);
        }

        // ─── Task 8.3: Fail descriptor routes when scene has no scene descriptor ───
        [Test]
        public void Test8_3_MissingSceneDescriptor_FailsWithClearError()
        {
            // Empty scene / no descriptor override
            MCPVRChatWorldCommands.TestSceneDescriptorOverride = null;

            var res = MCPVRChatWorldCommands.GetDescriptor(new Dictionary<string, object>()) as Dictionary<string, object>;
            Assert.IsNotNull(res);
            Assert.IsTrue(res.ContainsKey("error"));
            string error = res["error"].ToString();
            Assert.IsTrue(error.Contains("not configured as a VRChat world scene") || error.Contains("VRCSceneDescriptor"),
                "Error must state that the scene is not a VRChat world scene or lacks VRCSceneDescriptor");

            // Add-spawn should also fail cleanly
            var addRes = MCPVRChatWorldCommands.AddSpawn(new Dictionary<string, object>
            {
                { "name", "TestSpawn" }
            }) as Dictionary<string, object>;
            Assert.IsNotNull(addRes);
            Assert.IsTrue(addRes.ContainsKey("error"));
        }

        // ─── Task 8.4: Udon behaviour listing route ───
        [Test]
        public void Test8_4_UdonList_ReportsObjectPathAndProgramName()
        {
            var u1Go = new GameObject("DoorController");
            u1Go.transform.SetParent(_worldRoot.transform);
            var u1 = u1Go.AddComponent<MockUdonBehaviour>();
            u1.programSource = new TextAsset("DoorProgram") { name = "DoorProgramAsset" };
            u1.variables["isOpen"] = false;
            _cleanupObjects.Add(u1Go);
            _cleanupObjects.Add(u1.programSource);

            var u2Go = new GameObject("ScoreTracker");
            u2Go.transform.SetParent(_worldRoot.transform);
            var u2 = u2Go.AddComponent<MockUdonBehaviour>();
            u2.programSource = new TextAsset("ScoreProgram") { name = "ScoreProgramAsset" };
            u2.variables["score"] = 42;
            u2.variables["playerName"] = "Player1";
            _cleanupObjects.Add(u2Go);
            _cleanupObjects.Add(u2.programSource);

            var res = MCPVRChatWorldCommands.ListUdon(new Dictionary<string, object>()) as Dictionary<string, object>;
            Assert.IsNotNull(res);
            var behaviours = res["behaviours"] as List<Dictionary<string, object>>;
            Assert.IsNotNull(behaviours);
            Assert.IsTrue(behaviours.Count >= 2);

            var b1 = behaviours.Find(b => b["name"].ToString() == "DoorController");
            Assert.IsNotNull(b1);
            Assert.AreEqual("DoorProgramAsset", b1["programName"]);
            Assert.AreEqual(1, b1["variableCount"]);

            var b2 = behaviours.Find(b => b["name"].ToString() == "ScoreTracker");
            Assert.IsNotNull(b2);
            Assert.AreEqual("ScoreProgramAsset", b2["programName"]);
            Assert.AreEqual(2, b2["variableCount"]);
        }

        // ─── Task 8.5: Public variable read route ───
        [Test]
        public void Test8_5_UdonGetVariables_ReportsMixedTypes()
        {
            var uGo = new GameObject("GameSettings");
            uGo.transform.SetParent(_worldRoot.transform);
            var u = uGo.AddComponent<MockUdonBehaviour>();
            u.programSource = new TextAsset("GameSettingsProgram") { name = "GameSettingsProgram" };
            u.variables["maxPlayers"] = 32;
            u.variables["gameMode"] = "CaptureTheFlag";
            u.variables["isPVP"] = true;
            u.variables["gravity"] = 9.81f;
            u.variables["spawnOffset"] = new Vector3(0, 1.5f, 0);
            _cleanupObjects.Add(uGo);
            _cleanupObjects.Add(u.programSource);

            var res = MCPVRChatWorldCommands.GetUdonVariables(new Dictionary<string, object>
            {
                { "targetPath", MCPVRChatWorldCommands.GetGameObjectPath(uGo) }
            }) as Dictionary<string, object>;

            Assert.IsNotNull(res);
            Assert.IsTrue((bool)res["success"]);
            var vars = res["variables"] as List<Dictionary<string, object>>;
            Assert.IsNotNull(vars);
            Assert.AreEqual(5, vars.Count);

            var intVar = vars.Find(v => v["name"].ToString() == "maxPlayers");
            Assert.AreEqual("Int32", intVar["type"]);
            Assert.AreEqual(32, intVar["value"]);

            var strVar = vars.Find(v => v["name"].ToString() == "gameMode");
            Assert.AreEqual("String", strVar["type"]);
            Assert.AreEqual("CaptureTheFlag", strVar["value"]);

            var boolVar = vars.Find(v => v["name"].ToString() == "isPVP");
            Assert.AreEqual("Boolean", boolVar["type"]);
            Assert.AreEqual(true, boolVar["value"]);

            var vecVar = vars.Find(v => v["name"].ToString() == "spawnOffset");
            Assert.AreEqual("Vector3", vecVar["type"]);
            var vecVals = vecVar["value"] as float[];
            Assert.IsNotNull(vecVals);
            Assert.AreEqual(1.5f, vecVals[1]);
        }

        // ─── Task 8.6: Public variable write route ───
        [Test]
        public void Test8_6_UdonSetVariable_ValidWriteTakesEffect()
        {
            var uGo = new GameObject("WritableUdon");
            uGo.transform.SetParent(_worldRoot.transform);
            var u = uGo.AddComponent<MockUdonBehaviour>();
            u.variables["counter"] = 10;
            _cleanupObjects.Add(uGo);

            var res = MCPVRChatWorldCommands.SetUdonVariable(new Dictionary<string, object>
            {
                { "targetPath", MCPVRChatWorldCommands.GetGameObjectPath(uGo) },
                { "name", "counter" },
                { "value", 25 }
            }) as Dictionary<string, object>;

            Assert.IsNotNull(res);
            Assert.IsTrue((bool)res["success"]);
            Assert.AreEqual(25, u.variables["counter"]);
        }

        // ─── Task 8.7: Refuse write on type mismatch naming both types ───
        [Test]
        public void Test8_7_UdonSetVariable_TypeMismatchRefusedAndUnchanged()
        {
            var uGo = new GameObject("StrictUdon");
            uGo.transform.SetParent(_worldRoot.transform);
            var u = uGo.AddComponent<MockUdonBehaviour>();
            u.variables["counter"] = 10; // Int32
            _cleanupObjects.Add(uGo);

            // Attempt to write string to integer variable
            var res = MCPVRChatWorldCommands.SetUdonVariable(new Dictionary<string, object>
            {
                { "targetPath", MCPVRChatWorldCommands.GetGameObjectPath(uGo) },
                { "name", "counter" },
                { "value", "not_a_number" }
            }) as Dictionary<string, object>;

            Assert.IsNotNull(res);
            Assert.IsFalse((bool)res["success"]);
            Assert.IsTrue(res.ContainsKey("refused") && (bool)res["refused"]);
            Assert.AreEqual("Int32", res["expectedType"]);
            Assert.AreEqual("String", res["providedType"]);

            string err = res["error"].ToString();
            Assert.IsTrue(err.Contains("Int32") && err.Contains("String"), "Error must name both expected and provided types");

            // Variable MUST remain unchanged
            Assert.AreEqual(10, u.variables["counter"], "Variable value must remain unchanged after refusal");
        }

        // ─── Task 8.8: World validation route reporting findings with severity and affected object ───
        [Test]
        public void Test8_8_Validation_RunsAndReportsFindings()
        {
            MCPVRChatWorldCommands.TestVRWorldToolkitInstalledOverride = true;
            MCPVRChatWorldCommands.TestValidateOverride = (args) =>
            {
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "tool", "VRWorldToolkit" },
                    { "findings", new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                { "severity", "Warning" },
                                { "affectedObject", "Directional Light" },
                                { "message", "Realtime shadow mask missing" }
                            },
                            new Dictionary<string, object>
                            {
                                { "severity", "Error" },
                                { "affectedObject", "Main Camera" },
                                { "message", "Scene contains active Camera" }
                            }
                        }
                    }
                };
            };

            var res = MCPVRChatWorldCommands.Validate(new Dictionary<string, object>()) as Dictionary<string, object>;
            Assert.IsNotNull(res);
            Assert.IsTrue((bool)res["success"]);
            Assert.AreEqual("VRWorldToolkit", res["tool"]);

            var findings = res["findings"] as List<Dictionary<string, object>>;
            Assert.IsNotNull(findings);
            Assert.AreEqual(2, findings.Count);
            Assert.AreEqual("Warning", findings[0]["severity"]);
            Assert.AreEqual("Directional Light", findings[0]["affectedObject"]);
            Assert.AreEqual("Error", findings[1]["severity"]);
        }

        // ─── Task 8.9: Fail validation route naming dev.onevr.vrworldtoolkit when absent ───
        [Test]
        public void Test8_9_ValidationAbsent_FailsNamingPackage()
        {
            MCPVRChatWorldCommands.TestVRWorldToolkitInstalledOverride = false;

            var res = MCPVRChatWorldCommands.Validate(new Dictionary<string, object>()) as Dictionary<string, object>;
            Assert.IsNotNull(res);
            Assert.IsFalse((bool)res["success"]);
            Assert.IsTrue(res.ContainsKey("error"));

            string err = res["error"].ToString();
            Assert.IsTrue(err.Contains("dev.onevr.vrworldtoolkit"), "Error must name 'dev.onevr.vrworldtoolkit'");
            Assert.AreEqual("dev.onevr.vrworldtoolkit", res["requiredPackage"]);
        }

        // ─── Task 8.10: Content summary reports mirrors, lights, video players & flags unspatialized audio ───
        [Test]
        public void Test8_10_ContentSummary_FlagsUnspatializedAudio()
        {
            // The summary covers every loaded scene (the open project scene too), so assert deltas from a baseline.
            var before = (MCPVRChatWorldCommands.GetContentSummary(new Dictionary<string, object>()) as Dictionary<string, object>)["stats"] as Dictionary<string, object>;
            int Delta(Dictionary<string, object> after, string key) => (int)after[key] - (int)before[key];

            // 1. Mirror
            var mirrorGo = new GameObject("BathroomMirror");
            mirrorGo.transform.SetParent(_worldRoot.transform);
            mirrorGo.AddComponent<VRCMirrorReflection>();
            _cleanupObjects.Add(mirrorGo);

            // 2. Light
            var lightGo = new GameObject("DeskLamp");
            lightGo.transform.SetParent(_worldRoot.transform);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.shadows = LightShadows.Soft;
            _cleanupObjects.Add(lightGo);

            // 3. Video Player
            var vpGo = new GameObject("ProVideoPlayer");
            vpGo.transform.SetParent(_worldRoot.transform);
            vpGo.AddComponent<MockVideoPlayer>();
            _cleanupObjects.Add(vpGo);

            // 4. Properly spatialized audio source (3D with spatialize = true)
            var goodAudioGo = new GameObject("SpatialRadio");
            goodAudioGo.transform.SetParent(_worldRoot.transform);
            var goodAudio = goodAudioGo.AddComponent<AudioSource>();
            goodAudio.spatialize = true;
            goodAudio.spatialBlend = 1.0f;
            _cleanupObjects.Add(goodAudioGo);

            // 5. UNSPATIALIZED audio source (2D, spatialize = false)
            var badAudioGo = new GameObject("BackgroundBGM");
            badAudioGo.transform.SetParent(_worldRoot.transform);
            var badAudio = badAudioGo.AddComponent<AudioSource>();
            badAudio.spatialize = false;
            badAudio.spatialBlend = 0.0f;
            _cleanupObjects.Add(badAudioGo);

            var res = MCPVRChatWorldCommands.GetContentSummary(new Dictionary<string, object>()) as Dictionary<string, object>;
            Assert.IsNotNull(res);

            var stats = res["stats"] as Dictionary<string, object>;
            Assert.IsNotNull(stats);
            Assert.AreEqual(1, Delta(stats, "mirrorCount"));
            Assert.AreEqual(1, Delta(stats, "lightCount"));
            Assert.AreEqual(1, Delta(stats, "videoPlayerCount"));
            Assert.AreEqual(2, Delta(stats, "audioSourceCount"));
            Assert.AreEqual(1, Delta(stats, "unspatializedAudioCount"), "Exactly 1 unspatialized audio source must be counted");

            var unspatialized = (res["unspatializedAudio"] as List<Dictionary<string, object>>)
                .FindAll(a => a["path"].ToString().StartsWith(_worldRoot.name + "/"));
            Assert.AreEqual(1, unspatialized.Count);
            Assert.AreEqual("BackgroundBGM", unspatialized[0]["name"]);
        }

        // ─── Preview-scene objects (Scene view lights, prefab-stage clones) are not part of the world ───
        [Test]
        public void ContentSummary_IgnoresPreviewSceneObjects()
        {
            int LightCount() => (int)((MCPVRChatWorldCommands.GetContentSummary(new Dictionary<string, object>()) as Dictionary<string, object>)["stats"] as Dictionary<string, object>)["lightCount"];
            int before = LightCount();

            var preview = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            try
            {
                var go = new GameObject("PreviewOnlyLight", typeof(Light));
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, preview);
                Assert.AreEqual(before, LightCount());
            }
            finally
            {
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(preview);
            }
        }
    }

    // ─── Test Mock Components ───
    public class MockVRCSceneDescriptor : MonoBehaviour
    {
        // Mirrors VRC_SceneDescriptor.SpawnOrder: the SDK field is an enum, and set-spawns parses into it.
        public enum SpawnOrder { First, Sequential, Random, Demographic }

        public Transform[] spawns = new Transform[0];
        public SpawnOrder spawnOrder = SpawnOrder.Sequential;
        public float respawnHeightY = -100f;
        public GameObject referenceCamera = null;
        public bool forbidUserPortals = false;
    }

    public class MockUdonBehaviour : MonoBehaviour
    {
        public UnityEngine.Object programSource;
        public Dictionary<string, object> variables = new Dictionary<string, object>();
    }

    // Named after the SDK type: the content summary detects mirrors by exact type name.
    public class VRCMirrorReflection : MonoBehaviour
    {
    }

    public class MockVideoPlayer : MonoBehaviour
    {
    }
}
