# Unity 5.6 Codex Bridge

A Unity 5.6-compatible, file-based bridge. It uses Unity 5.6 APIs, requires no MCP server or window automation, and is intended for editor-side asset operations.

## Install

Copy `Assets/Editor/CodexBridge.cs` into the target project's `Assets/Editor/` directory, then open the project and let Unity compile it.

The bridge creates the following folders at the Unity project root:

```text
CodexBridge/Inbox
CodexBridge/Outbox
CodexBridge/Logs
```

Drop one JSON command into `CodexBridge/Inbox`. While the Unity editor is open, the bridge polls for commands, writes a result file to `CodexBridge/Outbox`, and renames the processed request to `*.json.done`.

`Tools > Codex Bridge 5.6 > Process Inbox Now` is available for a manual immediate run.

## Commands

`ping`, `refresh`, `createFolder`, `createMaterial`, `createPrefab`, `setAssetBundle`, `setAssetBundles`, `buildAssetBundles`, `inspectMaterial`, `replaceMaterialShader`, and `repairInvalidMaterialShaders`.

`repairInvalidMaterialShaders` changes only materials whose active runtime shader is `Hidden/InternalErrorShader`. It reads serialized texture metadata, preserves matching texture slots and UV scale/offset when assigning the target shader, and reports every modified material path in the result JSON.

## AssetBundle Notes

AssetBundle names are relative to `Build/abdata`; do not prefix them with `abdata/`. `buildAssetBundles` defaults to `Build/abdata`, `StandaloneWindows`, and chunk compression. Project postprocessing is enabled by default; use `skipPostprocess: true` when the target project does not provide the Koikatsu tooling classes.

See `examples/` for command payloads.
