using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace UnityMCP.Editor
{
    [TestFixture]
    public class MCPVRChatPoiyomiTests
    {
        private const string TempDir = "Assets/__MCPPoiyomiTestTemp";
        private Material _unlockedMat;
        private Material _lockedMat;
        private string _unlockedPath;
        private string _lockedPath;

        [SetUp]
        public void SetUp()
        {
            MCPVRChatPoiyomiCommands.ResetTestOverrides();
        }

        [TearDown]
        public void TearDown()
        {
            MCPVRChatPoiyomiCommands.ResetTestOverrides();
            AssetDatabase.DeleteAsset(TempDir);
        }

        // The routes resolve materials through the AssetDatabase (the wire only carries paths/names)
        // and reflect on Thry's ShaderOptimizer, so the materials must be real .mat assets on a real Poiyomi shader.
        private void CreatePoiMaterials()
        {
            var shader = Shader.Find(".poiyomi/Poiyomi Toon");
            if (shader == null || MCPVRChatPoiyomiCommands.ResolveOptimizerType(out _) == null)
                Assert.Ignore("Poiyomi (shader + Thry ShaderOptimizer) not present in this project.");

            if (!AssetDatabase.IsValidFolder(TempDir))
                AssetDatabase.CreateFolder("Assets", "__MCPPoiyomiTestTemp");

            _unlockedPath = TempDir + "/Test_Poi_Unlocked.mat";
            _unlockedMat = new Material(shader);
            AssetDatabase.CreateAsset(_unlockedMat, _unlockedPath);

            _lockedPath = TempDir + "/Test_Poi_Locked.mat";
            _lockedMat = new Material(shader);
            _lockedMat.SetOverrideTag("OriginalShader", ".poiyomi/Poiyomi Toon");
            _lockedMat.SetOverrideTag("AllLockedGUIDS", "mock_guid");
            AssetDatabase.CreateAsset(_lockedMat, _lockedPath);
        }

        // ─── Task 6.1: Discovery reports both locked and unlocked materials correctly ───
        [Test]
        public void Test6_1_StatusRoute_ReportsLockedAndUnlockedMaterials()
        {
            CreatePoiMaterials();
            MCPVRChatPoiyomiCommands.TestPoiyomiInstalledOverride = true;
            MCPVRChatPoiyomiCommands.TestIsLockedOverride = (mat) =>
            {
                if (mat == _lockedMat) return true;
                if (mat == _unlockedMat) return false;
                return null;
            };

            var args = new Dictionary<string, object>
            {
                { "materials", new List<object> { _unlockedPath, _lockedPath } }
            };

            var response = MCPVRChatPoiyomiCommands.GetStatus(args) as Dictionary<string, object>;
            Assert.IsNotNull(response);
            Assert.AreEqual(true, response["installed"]);
            Assert.AreEqual(2, response["totalCount"]);
            Assert.AreEqual(1, response["lockedCount"]);
            Assert.AreEqual(1, response["unlockedCount"]);

            var list = response["materials"] as List<Dictionary<string, object>>;
            Assert.IsNotNull(list);
            Assert.AreEqual(2, list.Count);

            var unl = list.Find(m => m["name"].ToString() == _unlockedMat.name);
            Assert.IsNotNull(unl);
            Assert.AreEqual(false, unl["isLocked"]);

            var lck = list.Find(m => m["name"].ToString() == _lockedMat.name);
            Assert.IsNotNull(lck);
            Assert.AreEqual(true, lck["isLocked"]);
        }

        // ─── Task 6.2: Fail with a "not installed" error naming Poiyomi when absent ───
        [Test]
        public void Test6_2_StatusRoute_FailsWhenPoiyomiAbsent_NamingPoiyomi()
        {
            MCPVRChatPoiyomiCommands.TestPoiyomiInstalledOverride = false;

            var response = MCPVRChatPoiyomiCommands.GetStatus(new Dictionary<string, object>()) as Dictionary<string, object>;
            Assert.IsNotNull(response);
            Assert.IsTrue(response.ContainsKey("error"), "Must return error when Poiyomi is absent.");
            Assert.IsTrue(response["error"].ToString().IndexOf("Poiyomi", StringComparison.OrdinalIgnoreCase) >= 0,
                $"Error message must explicitly name 'Poiyomi': {response["error"]}");
            Assert.AreEqual(false, response["installed"]);
        }

        // ─── Task 6.3: Reflection against optimizer type; missing member produces clear error ───
        [Test]
        public void Test6_3_LockRoute_MissingMemberProducesClearErrorNamingMember()
        {
            CreatePoiMaterials();
            MCPVRChatPoiyomiCommands.TestPoiyomiInstalledOverride = true;
            MCPVRChatPoiyomiCommands.TestMissingMemberOverride = "LockMaterials";

            var args = new Dictionary<string, object>
            {
                { "materials", new List<object> { _unlockedPath } }
            };

            var response = MCPVRChatPoiyomiCommands.Lock(args) as Dictionary<string, object>;
            Assert.IsNotNull(response);
            Assert.IsTrue(response.ContainsKey("error"), "Must return error when member is missing.");
            string err = response["error"].ToString();
            Assert.IsTrue(err.Contains("LockMaterials"), $"Error must name missing method: {err}");
            Assert.IsTrue(err.Contains("ShaderOptimizer"), $"Error must name optimizer type: {err}");
        }

        // ─── Task 6.4: Report materials already in requested state as unchanged; second unlock reports no-ops ───
        [Test]
        public void Test6_4_UnlockRoute_ReportsNoOpWhenAlreadyUnlocked()
        {
            CreatePoiMaterials();
            MCPVRChatPoiyomiCommands.TestPoiyomiInstalledOverride = true;
            MCPVRChatPoiyomiCommands.TestIsLockedOverride = (mat) => false; // Already unlocked

            var args = new Dictionary<string, object>
            {
                { "materials", new List<object> { _unlockedPath } }
            };

            var response = MCPVRChatPoiyomiCommands.Unlock(args) as Dictionary<string, object>;
            Assert.IsNotNull(response);
            Assert.AreEqual(true, response["success"]);
            Assert.AreEqual(0, response["changed"]);
            Assert.AreEqual(1, response["unchanged"]);
            Assert.AreEqual(0, response["failed"]);

            var results = response["results"] as List<Dictionary<string, object>>;
            Assert.IsNotNull(results);
            Assert.AreEqual(1, results.Count);
            Assert.AreEqual("unchanged", results[0]["status"]);
            Assert.IsTrue(results[0]["reason"].ToString().Contains("already unlocked"));
        }

        // ─── Task 6.5: Report per-material success and failure on partial batch failure ───
        [Test]
        public void Test6_5_LockRoute_PartialBatchFailureReportsPerMaterialReason()
        {
            CreatePoiMaterials();
            MCPVRChatPoiyomiCommands.TestPoiyomiInstalledOverride = true;
            MCPVRChatPoiyomiCommands.TestIsLockedOverride = (mat) => false; // Both need locking

            // Simulate one material succeeding and one failing
            MCPVRChatPoiyomiCommands.TestLockActionOverride = (materials, targetLock) =>
            {
                var list = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object> { { "material", _unlockedMat.name }, { "status", "locked" } },
                    new Dictionary<string, object> { { "material", "BrokenMat" }, { "status", "failed" }, { "reason", "Shader compilation error in generated pass" } }
                };
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "action", "lock" },
                    { "total", 2 },
                    { "changed", 1 },
                    { "unchanged", 0 },
                    { "failed", 1 },
                    { "results", list }
                };
            };

            var args = new Dictionary<string, object>
            {
                { "materials", new List<object> { _unlockedPath, "BrokenMat" } }
            };

            var response = MCPVRChatPoiyomiCommands.Lock(args) as Dictionary<string, object>;
            Assert.IsNotNull(response);
            Assert.AreEqual(false, response["success"]);
            Assert.AreEqual(1, response["changed"]);
            Assert.AreEqual(1, response["failed"]);

            var results = response["results"] as List<Dictionary<string, object>>;
            Assert.IsNotNull(results);
            Assert.AreEqual(2, results.Count);
            Assert.AreEqual("locked", results[0]["status"]);
            Assert.AreEqual("failed", results[1]["status"]);
            Assert.IsTrue(results[1]["reason"].ToString().Contains("Shader compilation error"));
        }

        // ─── Task 6.6: Property write on locked material refuses or auto-unlocks ───
        [Test]
        public void Test6_6_PropertyWrite_RefusedWhenLockedWithoutAutoUnlock()
        {
            CreatePoiMaterials();
            MCPVRChatPoiyomiCommands.TestPoiyomiInstalledOverride = true;
            MCPVRChatPoiyomiCommands.TestIsLockedOverride = (mat) => true; // Material is locked

            var args = new Dictionary<string, object>
            {
                { "material", _lockedPath },
                { "propertyName", "_Color" },
                { "value", new List<object> { 1.0, 0.0, 0.0, 1.0 } }, // JSON arrays arrive as List<object>
                { "unlockIfLocked", false }
            };

            var response = MCPVRChatPoiyomiCommands.SetProperty(args) as Dictionary<string, object>;
            Assert.IsNotNull(response);
            Assert.AreEqual(false, response["success"]);
            Assert.AreEqual(true, response["refused"]);
            Assert.AreEqual(false, response["unlockedFirst"]);
            Assert.IsTrue(response["error"].ToString().Contains("locked"));
        }

        [Test]
        public void Test6_6_PropertyWrite_AutoUnlocksWhenRequested()
        {
            CreatePoiMaterials();
            MCPVRChatPoiyomiCommands.TestPoiyomiInstalledOverride = true;
            bool isLocked = true;
            MCPVRChatPoiyomiCommands.TestIsLockedOverride = (mat) => isLocked;
            MCPVRChatPoiyomiCommands.TestLockActionOverride = (mats, targetLock) =>
            {
                isLocked = targetLock;
                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "action", "unlock" },
                    { "changed", 1 },
                    { "unchanged", 0 },
                    { "failed", 0 },
                    { "results", new List<Dictionary<string, object>> { new Dictionary<string, object> { { "material", _lockedPath }, { "status", "unlocked" } } } }
                };
            };

            var args = new Dictionary<string, object>
            {
                { "material", _lockedPath },
                { "propertyName", "_Color" },
                { "value", new List<object> { 1.0, 0.5, 0.0, 1.0 } },
                { "unlockIfLocked", true }
            };

            var response = MCPVRChatPoiyomiCommands.SetProperty(args) as Dictionary<string, object>;
            Assert.IsNotNull(response);
            Assert.AreEqual(true, response["success"]);
            Assert.AreEqual(false, response["refused"]);
            Assert.AreEqual(true, response["unlockedFirst"]);
            Assert.IsTrue(response.ContainsKey("message"));
            Assert.IsTrue(response["message"].ToString().Contains("unlocked first"));
        }
    }
}
