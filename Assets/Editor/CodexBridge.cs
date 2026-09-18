using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

#pragma warning disable 0649

[InitializeOnLoad]
public static class CodexBridge
{
    private const string BridgeRootName = "CodexBridge";
    private const double ProcessIntervalSeconds = 0.5d;
    private const string KoikatsuPathDefault = "C:/Illusion/Koikatu";
    private static readonly object LogLock = new object();
    private static readonly string ProjectRoot = Directory.GetParent(Application.dataPath).FullName;
    private static readonly string Root = Path.Combine(ProjectRoot, BridgeRootName);
    private static readonly string Inbox = Path.Combine(Root, "Inbox");
    private static readonly string Outbox = Path.Combine(Root, "Outbox");
    private static readonly string Logs = Path.Combine(Root, "Logs");
    private static readonly string UnityLog = Path.Combine(Logs, "unity-log.jsonl");
    private static string[] _resultFiles = new string[0];
    private static MaterialInfo _resultMaterial;
    private static double _nextProcessTime;
    private static bool _isProcessing;

    static CodexBridge()
    {
        EnsureBridgeFolders();
        Application.logMessageReceived -= OnLogMessageReceived;
        Application.logMessageReceived += OnLogMessageReceived;
        EditorApplication.update -= ProcessCommandsOnTick;
        EditorApplication.update += ProcessCommandsOnTick;
        Debug.Log("[CodexBridge56] Ready. Inbox: " + Inbox);
    }

    [MenuItem("Tools/Codex Bridge 5.6/Process Inbox Now")]
    public static void ProcessCommandsMenuItem()
    {
        ProcessCommands();
    }

    [MenuItem("Tools/Codex Bridge 5.6/Open Bridge Folder")]
    public static void OpenBridgeFolder()
    {
        EnsureBridgeFolders();
        EditorUtility.RevealInFinder(Root);
    }

    private static void ProcessCommandsOnTick()
    {
        if (EditorApplication.timeSinceStartup < _nextProcessTime)
            return;

        _nextProcessTime = EditorApplication.timeSinceStartup + ProcessIntervalSeconds;
        ProcessCommands();
    }

    private static void ProcessCommands()
    {
        if (_isProcessing)
            return;

        _isProcessing = true;
        try
        {
            EnsureBridgeFolders();
            string[] files = Directory.GetFiles(Inbox, "*.json");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
                ProcessCommandFile(file);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private static void ProcessCommandFile(string file)
    {
        if (!IsFileReady(file))
            return;

        Command command = null;
        string message = "OK";

        try
        {
            _resultFiles = new string[0];
            _resultMaterial = null;
            command = JsonUtility.FromJson<Command>(File.ReadAllText(file));

            if (command == null || string.IsNullOrEmpty(command.operation))
                throw new Exception("Invalid command: operation is empty.");

            switch (command.operation.Trim().ToLowerInvariant())
            {
                case "ping":
                    message = "pong";
                    break;

                case "refresh":
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                    message = "AssetDatabase refreshed.";
                    break;

                case "createfolder":
                    CreateFolder(command);
                    message = "Folder created or already exists: " + command.assetPath;
                    break;

                case "creatematerial":
                    CreateMaterial(command);
                    message = "Material created or updated: " + command.assetPath;
                    break;

                case "createprefab":
                    CreatePrefab(command);
                    message = "Prefab created or updated: " + command.assetPath;
                    break;

                case "setassetbundle":
                case "setassetbundles":
                    message = "AssetBundle assignment updated for " + SetAssetBundles(command) + " asset(s).";
                    break;

                case "buildassetbundles":
                    BuildAssetBundleResult buildResult = BuildAssetBundles(command);
                    _resultFiles = buildResult.changedFiles;
                    message = "Built/updated " + buildResult.changedFiles.Length + " asset bundle file(s) in " + buildResult.outputDirectory + ".";
                    break;

                case "inspectmaterial":
                    _resultMaterial = InspectMaterial(command.assetPath);
                    message = "Material inspected: " + command.assetPath;
                    break;

                case "replacematerialshader":
                    _resultMaterial = ReplaceMaterialShader(command);
                    message = "Material shader replaced: " + _resultMaterial.assetPath;
                    break;

                case "repairinvalidmaterialshaders":
                    _resultFiles = RepairInvalidMaterialShaders(command);
                    message = "Repaired " + _resultFiles.Length + " material shader(s).";
                    break;

                default:
                    throw new Exception("Unknown operation: " + command.operation);
            }

            WriteResult(file, command, true, message);
        }
        catch (Exception ex)
        {
            Debug.LogError("[CodexBridge56] Command failed: " + Path.GetFileName(file) + "\n" + ex);
            WriteResult(file, command, false, ex.ToString());
        }
    }

    private static void CreateFolder(Command command)
    {
        string path = NormalizeAssetPath(command.assetPath, "assetPath").TrimEnd('/');
        if (path != "Assets")
            EnsureAssetFolder(path);
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
    }

    private static void CreateMaterial(Command command)
    {
        string path = NormalizeAssetPath(command.assetPath, "assetPath");
        if (!path.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Material assetPath must end with .mat: " + path);

        EnsureParentFolder(path);
        Shader shader = Shader.Find(string.IsNullOrEmpty(command.shader) ? "Standard" : command.shader);
        if (shader == null)
            throw new Exception("Shader not found: " + command.shader);

        Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
        UnityEngine.Object existing = AssetDatabase.LoadAssetAtPath(path, typeof(UnityEngine.Object));
        if (material == null && existing != null)
            throw new Exception("Asset exists but is not a Material: " + path);

        if (material == null)
        {
            material = new Material(shader);
            material.name = Path.GetFileNameWithoutExtension(path);
            AssetDatabase.CreateAsset(material, path);
        }
        else
        {
            material.shader = shader;
        }

        ApplySimpleMaterialOverrides(material, command);
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
    }

    private static void CreatePrefab(Command command)
    {
        string path = NormalizeAssetPath(command.assetPath, "assetPath");
        if (!path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Prefab assetPath must end with .prefab: " + path);

        EnsureParentFolder(path);
        string objectName = string.IsNullOrEmpty(command.name) ? Path.GetFileNameWithoutExtension(path) : command.name;
        GameObject go = CreatePrefabSourceObject(command, objectName);
        try
        {
            GameObject prefab = PrefabUtility.CreatePrefab(path, go);
            if (prefab == null)
                throw new Exception("PrefabUtility.CreatePrefab returned null for " + path);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
        }
    }

    private static GameObject CreatePrefabSourceObject(Command command, string objectName)
    {
        bool wantsPrimitive = !string.IsNullOrEmpty(command.primitive) || !string.IsNullOrEmpty(command.materialPath);
        GameObject go = wantsPrimitive ? GameObject.CreatePrimitive(ParsePrimitiveType(command.primitive)) : new GameObject();
        go.name = objectName;

        Collider collider = go.GetComponent<Collider>();
        if (collider != null)
            UnityEngine.Object.DestroyImmediate(collider);

        if (!string.IsNullOrEmpty(command.materialPath))
        {
            string materialPath = NormalizeAssetPath(command.materialPath, "materialPath");
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
                throw new Exception("Material not found: " + materialPath);

            MeshRenderer renderer = go.GetComponent<MeshRenderer>();
            if (renderer == null)
                renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
        }

        return go;
    }

    private static MaterialInfo ReplaceMaterialShader(Command command)
    {
        string sourcePath = NormalizeAssetPath(command.assetPath, "assetPath");
        string targetPath = string.IsNullOrEmpty(command.outputAssetPath) ? sourcePath : NormalizeAssetPath(command.outputAssetPath, "outputAssetPath");
        string shaderName = string.IsNullOrEmpty(command.shader) ? "Standard" : command.shader;
        Shader shader = Shader.Find(shaderName);

        if (shader == null)
            throw new Exception("Shader not found: " + shaderName);

        Material source = AssetDatabase.LoadAssetAtPath<Material>(sourcePath);
        if (source == null)
            throw new Exception("Material not found: " + sourcePath);

        Material material;
        if (targetPath == sourcePath)
        {
            material = source;
        }
        else
        {
            EnsureParentFolder(targetPath);
            Material existing = AssetDatabase.LoadAssetAtPath<Material>(targetPath);
            if (existing != null)
            {
                material = existing;
                EditorUtility.CopySerialized(source, material);
            }
            else
            {
                material = new Material(source);
                material.name = Path.GetFileNameWithoutExtension(targetPath);
                AssetDatabase.CreateAsset(material, targetPath);
            }
        }

        Texture mainTex = GetFirstTexture(source, new string[] { "_MainTex", "_BaseMap" });
        Texture normalTex = GetFirstTexture(source, new string[] { "_NormalMap", "_BumpMap" });
        Color color = source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white;
        float glossiness = source.HasProperty("_Glossiness") ? source.GetFloat("_Glossiness") : 0.5f;
        float metallic = source.HasProperty("_Metallic") ? source.GetFloat("_Metallic") : 0f;

        material.shader = shader;

        if (mainTex != null && material.HasProperty("_MainTex"))
            material.SetTexture("_MainTex", mainTex);
        if (normalTex != null && material.HasProperty("_BumpMap"))
            material.SetTexture("_BumpMap", normalTex);
        if (normalTex != null && material.HasProperty("_NormalMap"))
            material.SetTexture("_NormalMap", normalTex);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", color);
        if (material.HasProperty("_Glossiness"))
            material.SetFloat("_Glossiness", glossiness);
        if (material.HasProperty("_Metallic"))
            material.SetFloat("_Metallic", metallic);

        ApplySimpleMaterialOverrides(material, command);
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(targetPath, ImportAssetOptions.ForceUpdate);
        return InspectMaterial(targetPath);
    }

    private static Texture GetFirstTexture(Material material, string[] names)
    {
        foreach (string name in names)
        {
            if (material.HasProperty(name))
            {
                Texture texture = material.GetTexture(name);
                if (texture != null)
                    return texture;
            }
        }
        return null;
    }

    private static string[] RepairInvalidMaterialShaders(Command command)
    {
        string folder = NormalizeAssetPath(command.assetPath, "assetPath");
        if (!AssetDatabase.IsValidFolder(folder))
            throw new Exception("assetPath must be an existing Assets folder: " + folder);

        string shaderName = string.IsNullOrEmpty(command.shader) ? "xukmi/TomBSDF" : command.shader;
        Shader shader = Shader.Find(shaderName);
        if (shader == null)
            throw new Exception("Shader not found: " + shaderName);

        string[] guids = AssetDatabase.FindAssets("t:Material", new string[] { folder });
        List<string> repaired = new List<string>();
        foreach (string guid in guids)
        {
            string materialPath = AssetDatabase.GUIDToAssetPath(guid);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null || material.shader == null || material.shader.name != "Hidden/InternalErrorShader")
                continue;

            string fullPath = Path.Combine(ProjectRoot, materialPath.Replace("/", Path.DirectorySeparatorChar.ToString()));
            MaterialInfo sourceInfo = ParseMaterialFile(materialPath, fullPath);
            material.shader = shader;
            ApplyParsedTextures(material, sourceInfo.textures);
            CopyFirstAvailableColor(material, sourceInfo.colors, "_PBRBaseColor", new string[] { "_PBRBaseColor", "_Color", "_BaseColor" });

            SetRequiredFloat(material, "_PBRMetallic", 0.1f);
            SetRequiredFloat(material, "_PBRRoughness", 0.45f);
            SetRequiredFloat(material, "_PBRDirectIntensity", 3.5f);
            EditorUtility.SetDirty(material);
            repaired.Add(materialPath);
        }

        AssetDatabase.SaveAssets();
        foreach (string materialPath in repaired)
            AssetDatabase.ImportAsset(materialPath, ImportAssetOptions.ForceUpdate);
        return repaired.ToArray();
    }

    private static void ApplyParsedTextures(Material material, TextureEntry[] textures)
    {
        foreach (TextureEntry textureEntry in textures)
        {
            if (!material.HasProperty(textureEntry.name) || string.IsNullOrEmpty(textureEntry.assetPath))
                continue;
            Texture texture = AssetDatabase.LoadAssetAtPath<Texture>(textureEntry.assetPath);
            if (texture == null)
                continue;
            material.SetTexture(textureEntry.name, texture);
            material.SetTextureScale(textureEntry.name, new Vector2(textureEntry.scaleX, textureEntry.scaleY));
            material.SetTextureOffset(textureEntry.name, new Vector2(textureEntry.offsetX, textureEntry.offsetY));
        }
    }

    private static void CopyFirstAvailableColor(Material material, ColorEntry[] colors, string targetProperty, string[] sourceProperties)
    {
        if (!material.HasProperty(targetProperty)) return;
        foreach (string sourceProperty in sourceProperties)
        {
            foreach (ColorEntry color in colors)
            {
                if (color.name != sourceProperty) continue;
                material.SetColor(targetProperty, new Color(color.r, color.g, color.b, color.a));
                return;
            }
        }
    }

    private static void SetRequiredFloat(Material material, string property, float value)
    {
        if (!material.HasProperty(property))
            throw new Exception("Target shader does not expose required property: " + property);
        material.SetFloat(property, value);
    }

    private static void ApplySimpleMaterialOverrides(Material material, Command command)
    {
        if (!string.IsNullOrEmpty(command.color))
        {
            string property = string.IsNullOrEmpty(command.colorProperty) ? "_Color" : command.colorProperty;
            Color color;
            if (!ColorUtility.TryParseHtmlString(command.color, out color))
                throw new Exception("Invalid color: " + command.color);
            if (material.HasProperty(property))
                material.SetColor(property, color);
        }

        if (!string.IsNullOrEmpty(command.mainTexture))
        {
            string property = string.IsNullOrEmpty(command.textureProperty) ? "_MainTex" : command.textureProperty;
            Texture texture = AssetDatabase.LoadAssetAtPath<Texture>(NormalizeAssetPath(command.mainTexture, "mainTexture"));
            if (texture == null)
                throw new Exception("Texture not found: " + command.mainTexture);
            if (material.HasProperty(property))
                material.SetTexture(property, texture);
        }

        if (!string.IsNullOrEmpty(command.floatProperty) && material.HasProperty(command.floatProperty))
            material.SetFloat(command.floatProperty, command.floatValue);
    }

    private static MaterialInfo InspectMaterial(string assetPath)
    {
        string normalizedPath = NormalizeAssetPath(assetPath, "assetPath");
        string fullPath = Path.Combine(ProjectRoot, normalizedPath.Replace("/", Path.DirectorySeparatorChar.ToString()));
        if (!File.Exists(fullPath))
            throw new Exception("Material file not found: " + normalizedPath);

        Material material = AssetDatabase.LoadAssetAtPath<Material>(normalizedPath);
        MaterialInfo info = ParseMaterialFile(normalizedPath, fullPath);
        if (material != null)
        {
            info.runtimeShaderName = material.shader == null ? string.Empty : material.shader.name;
            info.renderQueue = material.renderQueue;
        }
        return info;
    }

    private static MaterialInfo ParseMaterialFile(string assetPath, string fullPath)
    {
        MaterialInfo info = new MaterialInfo();
        info.assetPath = assetPath;
        info.textures = new TextureEntry[0];
        info.floats = new FloatEntry[0];
        info.colors = new ColorEntry[0];

        string[] lines = File.ReadAllLines(fullPath);
        List<TextureEntry> textures = new List<TextureEntry>();
        List<FloatEntry> floats = new List<FloatEntry>();
        List<ColorEntry> colors = new List<ColorEntry>();
        string section = string.Empty;
        string currentTexture = string.Empty;
        TextureEntry currentTextureEntry = null;

        Regex shaderRegex = new Regex(@"m_Shader:\s*\{fileID:\s*([^,]+),\s*guid:\s*([^,]+),\s*type:\s*([^\}]+)\}");
        Regex textureNameRegex = new Regex(@"^\s*-\s+([^:]+):\s*$");
        Regex textureGuidRegex = new Regex(@"m_Texture:\s*\{fileID:\s*([^,]+),\s*guid:\s*([^,\}]+)");
        Regex textureScaleRegex = new Regex(@"m_Scale:\s*\{x:\s*([-+0-9.eE]+),\s*y:\s*([-+0-9.eE]+)\}");
        Regex textureOffsetRegex = new Regex(@"m_Offset:\s*\{x:\s*([-+0-9.eE]+),\s*y:\s*([-+0-9.eE]+)\}");
        Regex floatRegex = new Regex(@"^\s*-\s+([^:]+):\s*([-+0-9.eE]+)\s*$");
        Regex colorRegex = new Regex(@"^\s*-\s+([^:]+):\s*\{r:\s*([-+0-9.eE]+),\s*g:\s*([-+0-9.eE]+),\s*b:\s*([-+0-9.eE]+),\s*a:\s*([-+0-9.eE]+)\}");

        foreach (string line in lines)
        {
            Match shaderMatch = shaderRegex.Match(line);
            if (shaderMatch.Success)
            {
                info.shaderGuid = shaderMatch.Groups[2].Value.Trim();
                info.shaderAssetPath = AssetDatabase.GUIDToAssetPath(info.shaderGuid);
                Shader shader = string.IsNullOrEmpty(info.shaderAssetPath) ? null : AssetDatabase.LoadAssetAtPath<Shader>(info.shaderAssetPath);
                info.serializedShaderName = shader == null ? string.Empty : shader.name;
            }

            if (line.IndexOf("m_TexEnvs:", StringComparison.Ordinal) >= 0) { section = "tex"; continue; }
            if (line.IndexOf("m_Floats:", StringComparison.Ordinal) >= 0) { section = "float"; continue; }
            if (line.IndexOf("m_Colors:", StringComparison.Ordinal) >= 0) { section = "color"; continue; }

            if (section == "tex")
            {
                Match nameMatch = textureNameRegex.Match(line);
                if (nameMatch.Success)
                {
                    currentTexture = nameMatch.Groups[1].Value.Trim();
                    currentTextureEntry = null;
                    continue;
                }

                Match guidMatch = textureGuidRegex.Match(line);
                if (guidMatch.Success && !string.IsNullOrEmpty(currentTexture))
                {
                    string guid = guidMatch.Groups[2].Value.Trim();
                    if (guid == "0" || guid == "")
                        continue;
                    TextureEntry entry = new TextureEntry();
                    entry.name = currentTexture;
                    entry.guid = guid;
                    entry.assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    entry.scaleX = 1f;
                    entry.scaleY = 1f;
                    entry.offsetX = 0f;
                    entry.offsetY = 0f;
                    textures.Add(entry);
                    currentTextureEntry = entry;
                    continue;
                }

                if (currentTextureEntry != null)
                {
                    Match scaleMatch = textureScaleRegex.Match(line);
                    if (scaleMatch.Success)
                    {
                        currentTextureEntry.scaleX = ParseFloat(scaleMatch.Groups[1].Value);
                        currentTextureEntry.scaleY = ParseFloat(scaleMatch.Groups[2].Value);
                    }
                    Match offsetMatch = textureOffsetRegex.Match(line);
                    if (offsetMatch.Success)
                    {
                        currentTextureEntry.offsetX = ParseFloat(offsetMatch.Groups[1].Value);
                        currentTextureEntry.offsetY = ParseFloat(offsetMatch.Groups[2].Value);
                    }
                }
            }
            else if (section == "float")
            {
                Match m = floatRegex.Match(line);
                if (m.Success)
                {
                    FloatEntry entry = new FloatEntry();
                    entry.name = m.Groups[1].Value.Trim();
                    entry.value = ParseFloat(m.Groups[2].Value);
                    floats.Add(entry);
                }
            }
            else if (section == "color")
            {
                Match m = colorRegex.Match(line);
                if (m.Success)
                {
                    ColorEntry entry = new ColorEntry();
                    entry.name = m.Groups[1].Value.Trim();
                    entry.r = ParseFloat(m.Groups[2].Value);
                    entry.g = ParseFloat(m.Groups[3].Value);
                    entry.b = ParseFloat(m.Groups[4].Value);
                    entry.a = ParseFloat(m.Groups[5].Value);
                    colors.Add(entry);
                }
            }
        }

        info.textures = textures.ToArray();
        info.floats = floats.ToArray();
        info.colors = colors.ToArray();
        return info;
    }

    private static float ParseFloat(string value)
    {
        float result;
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        return result;
    }

    private static int SetAssetBundles(Command command)
    {
        int assignedCount = 0;
        if (command.assets != null && command.assets.Length > 0)
        {
            foreach (AssetBundleAssignment assignment in command.assets)
            {
                if (assignment == null) continue;
                SetAssetBundle(assignment.assetPath, string.IsNullOrEmpty(assignment.assetBundleName) ? command.assetBundleName : assignment.assetBundleName, string.IsNullOrEmpty(assignment.assetBundleVariant) ? command.assetBundleVariant : assignment.assetBundleVariant);
                assignedCount++;
            }
        }
        else if (command.assetPaths != null && command.assetPaths.Length > 0)
        {
            foreach (string path in command.assetPaths)
            {
                SetAssetBundle(path, command.assetBundleName, command.assetBundleVariant);
                assignedCount++;
            }
        }
        else
        {
            SetAssetBundle(command.assetPath, command.assetBundleName, command.assetBundleVariant);
            assignedCount++;
        }
        AssetDatabase.RemoveUnusedAssetBundleNames();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        return assignedCount;
    }

    private static void SetAssetBundle(string assetPath, string assetBundleName, string assetBundleVariant)
    {
        string normalizedAssetPath = NormalizeAssetPath(assetPath, "assetPath");
        if (AssetDatabase.IsValidFolder(normalizedAssetPath))
            throw new Exception("assetPath must be an asset file, not a folder: " + normalizedAssetPath);
        AssetImporter importer = AssetImporter.GetAtPath(normalizedAssetPath);
        if (importer == null)
            throw new Exception("AssetImporter not found: " + normalizedAssetPath);
        importer.assetBundleName = NormalizeAssetBundleName(assetBundleName);
        importer.assetBundleVariant = NormalizeAssetBundleVariant(assetBundleVariant);
        AssetDatabase.ImportAsset(normalizedAssetPath, ImportAssetOptions.ForceUpdate);
    }

    private static BuildAssetBundleResult BuildAssetBundles(Command command)
    {
        string relativeOutputDirectory = NormalizeProjectRelativePath(string.IsNullOrEmpty(command.outputDirectory) ? "Build/abdata" : command.outputDirectory, "outputDirectory");
        string absoluteOutputDirectory = Path.GetFullPath(Path.Combine(ProjectRoot, relativeOutputDirectory));
        if (!absoluteOutputDirectory.StartsWith(Path.GetFullPath(ProjectRoot), StringComparison.OrdinalIgnoreCase))
            throw new Exception("outputDirectory must stay inside the Unity project: " + relativeOutputDirectory);
        Directory.CreateDirectory(absoluteOutputDirectory);

        BuildAssetBundleOptions options = BuildAssetBundleOptions.None;
        if (!command.uncompressed) options |= BuildAssetBundleOptions.ChunkBasedCompression;
        if (command.forceRebuild) options |= BuildAssetBundleOptions.ForceRebuildAssetBundle;
        if (command.strictMode) options |= BuildAssetBundleOptions.StrictMode;
        if (command.disableWriteTypeTree) options |= BuildAssetBundleOptions.DisableWriteTypeTree;
        if (command.ignoreTypeTreeChanges) options |= BuildAssetBundleOptions.IgnoreTypeTreeChanges;

        BuildTarget buildTarget = ParseBuildTarget(command.buildTarget);
        DateTime prebuildTime = DateTime.Now;
        PreviewShaders.SetAllMaterialsOriginal();
        AssetBundleManifest manifest;
        try
        {
            manifest = BuildPipeline.BuildAssetBundles(absoluteOutputDirectory, options, buildTarget);
            if (manifest == null)
                throw new Exception("BuildPipeline.BuildAssetBundles returned null. Check Unity logs for the build error.");
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }
        finally
        {
            PreviewShaders.SetAllMaterialsPreview();
        }

        string[] allFiles = Directory.GetFiles(absoluteOutputDirectory, "*.unity3d", SearchOption.AllDirectories);
        List<string> changedFiles = new List<string>();
        foreach (string path in allFiles)
            if (File.GetLastWriteTime(path) >= prebuildTime)
                changedFiles.Add(path);
        changedFiles.Sort(StringComparer.OrdinalIgnoreCase);

        if (!command.skipPostprocess && changedFiles.Count > 0)
        {
            string koikatsuPath = string.IsNullOrEmpty(command.koikatsuPath) ? KoikatsuPathDefault : command.koikatsuPath;
            IllusionMods.KoikatuModdingTools.SB3UScript.BuildAndRunScripts(relativeOutputDirectory, koikatsuPath, !command.uncompressed, changedFiles);
        }

        BuildAssetBundleResult result = new BuildAssetBundleResult();
        result.outputDirectory = absoluteOutputDirectory;
        result.changedFiles = changedFiles.ToArray();
        return result;
    }

    private static BuildTarget ParseBuildTarget(string buildTarget)
    {
        if (string.IsNullOrEmpty(buildTarget)) return BuildTarget.StandaloneWindows;
        try { return (BuildTarget)Enum.Parse(typeof(BuildTarget), buildTarget, true); }
        catch { throw new Exception("Unsupported buildTarget: " + buildTarget); }
    }

    private static PrimitiveType ParsePrimitiveType(string primitive)
    {
        if (string.IsNullOrEmpty(primitive)) return PrimitiveType.Cube;
        try { return (PrimitiveType)Enum.Parse(typeof(PrimitiveType), primitive, true); }
        catch { throw new Exception("Unsupported primitive: " + primitive); }
    }

    private static void EnsureParentFolder(string assetPath)
    {
        string folder = Path.GetDirectoryName(assetPath);
        if (string.IsNullOrEmpty(folder)) throw new Exception("assetPath has no parent folder: " + assetPath);
        EnsureAssetFolder(folder.Replace("\\", "/"));
    }

    private static void EnsureAssetFolder(string folder)
    {
        folder = NormalizeAssetPath(folder, "folder").TrimEnd('/');
        if (AssetDatabase.IsValidFolder(folder)) return;
        string[] parts = folder.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }

    private static string NormalizeAssetPath(string path, string fieldName)
    {
        if (string.IsNullOrEmpty(path)) throw new Exception(fieldName + " is empty.");
        path = path.Trim().Replace("\\", "/");
        if (path.Contains(":") || path.StartsWith("/", StringComparison.Ordinal) || path.Contains("/../") || path.StartsWith("../", StringComparison.Ordinal) || path.EndsWith("/..", StringComparison.Ordinal))
            throw new Exception(fieldName + " must be a relative Assets path: " + path);
        if (path != "Assets" && !path.StartsWith("Assets/", StringComparison.Ordinal))
            throw new Exception(fieldName + " must start with Assets/: " + path);
        return path;
    }

    private static string NormalizeProjectRelativePath(string path, string fieldName)
    {
        if (string.IsNullOrEmpty(path)) throw new Exception(fieldName + " is empty.");
        path = path.Trim().Replace("\\", "/");
        if (path.Contains(":") || path.StartsWith("/", StringComparison.Ordinal) || path.Contains("/../") || path.StartsWith("../", StringComparison.Ordinal) || path.EndsWith("/..", StringComparison.Ordinal))
            throw new Exception(fieldName + " must be a project-relative path: " + path);
        return path;
    }

    private static string NormalizeAssetBundleName(string assetBundleName)
    {
        if (string.IsNullOrEmpty(assetBundleName)) return string.Empty;
        assetBundleName = assetBundleName.Trim().Replace("\\", "/").ToLowerInvariant();
        if (assetBundleName.Contains(":") || assetBundleName.StartsWith("/", StringComparison.Ordinal) || assetBundleName.Contains("/../") || assetBundleName.StartsWith("../", StringComparison.Ordinal) || assetBundleName.EndsWith("/..", StringComparison.Ordinal))
            throw new Exception("assetBundleName must be a relative bundle path: " + assetBundleName);
        if (assetBundleName == "abdata" || assetBundleName.StartsWith("abdata/", StringComparison.Ordinal))
            throw new Exception("assetBundleName must be relative to Build/abdata and must not start with abdata/: " + assetBundleName);
        return assetBundleName;
    }

    private static string NormalizeAssetBundleVariant(string assetBundleVariant)
    {
        if (string.IsNullOrEmpty(assetBundleVariant)) return string.Empty;
        assetBundleVariant = assetBundleVariant.Trim().ToLowerInvariant();
        if (assetBundleVariant.Contains("/") || assetBundleVariant.Contains("\\") || assetBundleVariant.Contains(":"))
            throw new Exception("assetBundleVariant must be a simple variant name: " + assetBundleVariant);
        return assetBundleVariant;
    }

    private static bool IsFileReady(string file)
    {
        if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalMilliseconds < 200d) return false;
        try { using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None)) return true; }
        catch (IOException) { return false; }
    }

    private static void WriteResult(string commandFile, Command command, bool success, string message)
    {
        EnsureBridgeFolders();
        string fileName = Path.GetFileNameWithoutExtension(commandFile);
        string resultPath = Path.Combine(Outbox, fileName + ".result.json");
        Result result = new Result();
        result.success = success;
        result.operation = command == null ? string.Empty : command.operation;
        result.assetPath = command == null ? string.Empty : command.assetPath;
        result.message = message;
        result.projectRoot = ProjectRoot;
        result.bridgeRoot = Root;
        result.files = _resultFiles;
        result.material = _resultMaterial;
        result.time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        File.WriteAllText(resultPath, JsonUtility.ToJson(result, true));
        string donePath = commandFile + ".done";
        if (File.Exists(donePath)) File.Delete(donePath);
        File.Move(commandFile, donePath);
    }

    private static void OnLogMessageReceived(string condition, string stackTrace, LogType type)
    {
        try
        {
            EnsureBridgeFolders();
            LogEntry entry = new LogEntry();
            entry.time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            entry.type = type.ToString();
            entry.message = condition;
            entry.stackTrace = stackTrace;
            lock (LogLock) File.AppendAllText(UnityLog, JsonUtility.ToJson(entry) + Environment.NewLine);
        }
        catch { }
    }

    private static void EnsureBridgeFolders()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Inbox);
        Directory.CreateDirectory(Outbox);
        Directory.CreateDirectory(Logs);
    }

    [Serializable]
    private class Command
    {
        public string operation;
        public string assetPath;
        public string outputAssetPath;
        public string shader;
        public string color;
        public string colorProperty;
        public string mainTexture;
        public string textureProperty;
        public string floatProperty;
        public float floatValue;
        public string name;
        public string materialPath;
        public string primitive;
        public string assetBundleName;
        public string assetBundleVariant;
        public string[] assetPaths;
        public AssetBundleAssignment[] assets;
        public string outputDirectory;
        public string buildTarget;
        public bool uncompressed;
        public bool forceRebuild;
        public bool strictMode;
        public bool disableWriteTypeTree;
        public bool ignoreTypeTreeChanges;
        public bool skipPostprocess;
        public string koikatsuPath;
    }

    [Serializable]
    private class AssetBundleAssignment
    {
        public string assetPath;
        public string assetBundleName;
        public string assetBundleVariant;
    }

    private class BuildAssetBundleResult
    {
        public string outputDirectory;
        public string[] changedFiles;
    }

    [Serializable]
    private class Result
    {
        public bool success;
        public string operation;
        public string assetPath;
        public string message;
        public string projectRoot;
        public string bridgeRoot;
        public string[] files;
        public MaterialInfo material;
        public string time;
    }

    [Serializable]
    private class MaterialInfo
    {
        public string assetPath;
        public string shaderGuid;
        public string shaderAssetPath;
        public string serializedShaderName;
        public string runtimeShaderName;
        public int renderQueue;
        public TextureEntry[] textures;
        public FloatEntry[] floats;
        public ColorEntry[] colors;
    }

    [Serializable]
    private class TextureEntry
    {
        public string name;
        public string guid;
        public string assetPath;
        public float scaleX;
        public float scaleY;
        public float offsetX;
        public float offsetY;
    }

    [Serializable]
    private class FloatEntry
    {
        public string name;
        public float value;
    }

    [Serializable]
    private class ColorEntry
    {
        public string name;
        public float r;
        public float g;
        public float b;
        public float a;
    }

    [Serializable]
    private class LogEntry
    {
        public string time;
        public string type;
        public string message;
        public string stackTrace;
    }
}
