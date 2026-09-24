<p align="center">
  <img src="icon.png" alt="AnkleBreaker MCP" width="140" />
</p>

# Unity MCP Plugin: VRChat fork

> **This is a fork of [AnkleBreaker-Studio/unity-mcp-plugin](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin).** It keeps everything upstream does and adds VRChat avatar and world tooling. Powered by AnkleBreaker MCP.

It's a UPM package that runs a local HTTP bridge (`127.0.0.1:7890`) inside the Unity Editor. It pairs with the server fork: **[unity-mcp-server-vrc](https://github.com/kirin-3/unity-mcp-server-vrc)**.

For the full list of Unity features, see the [upstream README](https://github.com/AnkleBreaker-Studio/unity-mcp-plugin#readme).

## Changes in this fork (v2.40.0)

- **31 `vrc/*` routes** (369 routes in total):
  - **Avatar:** performance rank (PC and Quest), 256-bit parameter budget, Write Defaults and texture audit, descriptor, visemes, playable layers, parameters, menus, PhysBones, contacts, and Modular Avatar and VRCFury components.
  - **Bake harness:** runs NDMF, MA, VRCFury, and d4rk on a temporary clone so the metrics are real post-bake numbers. Your scene is never mutated.
  - **Poiyomi:** status, lock/unlock, and get/set property.
  - **World:** descriptor and spawns, Udon behaviours and type-safe variable writes, VRWorldToolkit validation, and a content summary.
  - **Project context:** detects avatar, world, or none, plus the ecosystem packages installed.
- **VRChat safety guards (`MCPVRChatGuard`):** on a VRChat project, `build/start`, `settings/set-player`, `settings/set-quality-level`, and `settings/set-physics` are refused, as are layer and collision-matrix edits on reserved layers 0–22. Pass `override: true` on a single call to run one anyway. The guards run inside the route handlers, so no other call path skips them.
- **No hard dependency:** the plugin reaches the VRChat SDK, NDMF, MA, VRCFury, and VRWorldToolkit through reflection, so it compiles and runs without them. In a non-VRChat project the VRChat routes just report that the package is missing.
- **Protocol v2:** `ping` reports `protocolVersion: 2`.
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
