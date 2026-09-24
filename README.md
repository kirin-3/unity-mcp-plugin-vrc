<p align="center">
  <img src="icon.png" alt="AnkleBreaker MCP" width="140" />
</p>

# Unity MCP Plugin: VRChat fork

> **This is a fork of [AnkleBreaker-Studio/unity-mcp-plugin](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin).** It keeps everything upstream does and adds VRChat avatar and world tooling. Powered by AnkleBreaker MCP.

It's a UPM package that runs a local HTTP bridge (`127.0.0.1:7890`) inside the Unity Editor. It pairs with the server fork: **[unity-mcp-server-vrc](https://github.com/kirin-3/unity-mcp-server-vrc)**.

For the full list of Unity features, see the [upstream README](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin#readme).

## Changes in this fork (v2.42.0)

- **43 `vrc/*` routes** (381 routes in total):
  - **Avatar:** performance rank (PC and Quest), 256-bit parameter budget, descriptor, visemes, playable layers, parameters, menus, PhysBones, contacts, and Modular Avatar and VRCFury components.
  - **Audit:** Write Defaults, missing scripts, texture memory, animation paths that no longer resolve, mismatched mesh bounds and Anchor Overrides.
  - **VRCFury features:** create or update a Toggle (objects, blendshapes, material swaps, menu path, saved, default) and Armature Link.
  - **Outfit attach:** puts a clothing prefab under the avatar, merges it with MA Merge Armature or VRCFury Armature Link, and reports the bones that won't merge and why.
  - **Play-mode testing:** sets parameters and gestures through Gesture Manager or Av3Emulator, then captures the avatar.
  - **Blendshapes:** list and set by name, with Unified Expressions, ARKit and SRanipal face-tracking coverage.
  - **Bake harness:** runs NDMF, MA, VRCFury, and d4rk on a temporary clone so the metrics are real post-bake numbers. Your scene is never mutated.
  - **Poiyomi:** status, lock/unlock, and get/set property.
  - **World:** descriptor and spawns, Udon behaviours and type-safe variable writes, UdonSharp behaviour creation (script, program asset and component), VRWorldToolkit validation, and a content summary.
  - **Build:** `vrc/build` runs the VRChat SDK's own Build, or Build & Test, and reports validation and preprocessor errors and the bundle size. It never uploads.
  - **Project context:** detects avatar, world, or none, plus the ecosystem packages installed.
- **VRChat safety guards (`MCPVRChatGuard`):** on a VRChat project, `build/start`, `settings/set-player`, `settings/set-quality-level`, and `settings/set-physics` are refused, as are layer and collision-matrix edits on reserved layers 0–22. Pass `override: true` on a single call to run one anyway. The guards run inside the route handlers, so no other call path skips them.
- **No hard dependency:** the plugin reaches the VRChat SDK, NDMF, MA, VRCFury, VRWorldToolkit, Gesture Manager, Av3Emulator, and UdonSharp through reflection, so it compiles and runs without them. In a non-VRChat project the VRChat routes just report that the package is missing.
- **Protocol v5:** `ping` reports `protocolVersion: 5`. The server hides the tools a plugin's protocol can't answer.
- **Fix:** `MiniJson` stops reflection walks at a fixed depth, so serializing a `Bounds` no longer hangs the editor.
- **Tests:** VRChat EditMode tests under `Tests/Editor`, plus a `vrc` self-test category.

See [CHANGELOG.md](CHANGELOG.md) for details.

## Install

In Unity, go to **Window > Package Manager > + > Add package from git URL** and enter:

```
https://github.com/kirin-3/unity-mcp-plugin-vrc.git
```

The console should print `[MCP Bridge] Server started on port 7890`. Settings live under **Window > MCP Dashboard**.

Requires Unity 2021.3+ (VRChat projects use 2022.3). No route uploads or publishes anything to VRChat.

## License

AnkleBreaker Open License v1.0 (see [LICENSE](LICENSE)). **Powered by AnkleBreaker MCP.** Free to use; reselling is not allowed.
