using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using Lyuma.Av3Emulator.Runtime;
using System.Linq;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using System.Threading.Tasks;

namespace Narazaka.VRChat.ParameterIconGenerator.Editor
{
    // [CustomEditor(typeof(ParameterIconGenerator))]
    public class ParameterIconGenerator : EditorWindow
    {
        static int layer = 31;
        static int cullingMask = 1 << layer;

        static int[] sizes = { 64, 128, 256, 512 };

        [SerializeField] int size = 256;
        [SerializeField] bool useMaterialName;
        [SerializeField] int selectedIntIndex = 0;
        [SerializeField] Renderer materialNameSource;

        Camera camera;
        RenderTexture renderTexture;
        LyumaAv3Runtime emulator;
        string dir = "Assets";

        GameObject[] renderTargets;
        VRCExpressionsMenu.Control[] targetControls;
        int targetControlIndex = -1;
        int afterParameterSet = int.MaxValue;

        SerializedObject serializedObject;
        SerializedProperty sizeProperty;
        SerializedProperty useMaterialNameProperty;
        SerializedProperty selectedIntIndexProperty;
        SerializedProperty materialNameSourceProperty;

        [MenuItem("Tools/Parameter Icon Generator")]
        public static void ShowWindow()
        {
            GetWindow<ParameterIconGenerator>(nameof(ParameterIconGenerator));
        }

        void OnEnable()
        {
            serializedObject = new SerializedObject(this);
            sizeProperty = serializedObject.FindProperty(nameof(size));
            useMaterialNameProperty = serializedObject.FindProperty(nameof(useMaterialName));
            selectedIntIndexProperty = serializedObject.FindProperty(nameof(selectedIntIndex));
            materialNameSourceProperty = serializedObject.FindProperty(nameof(materialNameSource));
            SceneView.duringSceneGui += OnSceneGUI;
        }

        void OnDisable()
        {
            if (renderTexture != null) Object.DestroyImmediate(renderTexture);
            if (camera != null) DestroyImmediate(camera.gameObject);
            SceneView.duringSceneGui -= OnSceneGUI;
        }

        void OnGUI()
        {
            if (!EditorApplication.isPlaying)
            {
                EditorGUILayout.HelpBox("Please enter play mode.", MessageType.Info);
                return;
            }

            if (camera == null)
            {
                var obj = new GameObject("ParameterIconGeneratorCamera", typeof(Camera));
                obj.transform.position = new Vector3(0, 1, 1);
                obj.transform.rotation = Quaternion.Euler(0, 180, 0);
                camera = obj.GetComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.fieldOfView = 30;
                camera.nearClipPlane = 0.00001f;
                camera.farClipPlane = 3;
                camera.enabled = false;
            }

            EditorGUILayout.ObjectField("Camera", camera, typeof(Camera), true);
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                var cameraPosition = EditorGUILayout.Vector3Field("Camera Position", camera.transform.position);
                if (check.changed)
                {
                    camera.transform.position = cameraPosition;
                }
            }
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                var cameraRotation = EditorGUILayout.Vector3Field("Camera Rotation", camera.transform.rotation.eulerAngles);
                if (check.changed)
                {
                    camera.transform.rotation = Quaternion.Euler(cameraRotation);
                }
            }

            serializedObject.UpdateIfRequiredOrScript();

            EditorGUILayout.PropertyField(useMaterialNameProperty, new GUIContent("Use Material Name (else use Menu name)"));
            if (useMaterialNameProperty.boolValue)
            {
                EditorGUILayout.PropertyField(materialNameSourceProperty);
                if (materialNameSourceProperty.objectReferenceValue == null)
                {
                    EditorGUILayout.HelpBox("using first renderer's material", MessageType.Info);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                var sizeChanged = false;
                using (var check = new EditorGUI.ChangeCheckScope())
                {
                    EditorGUILayout.PropertyField(sizeProperty);
                    if (check.changed || renderTexture == null)
                    {
                        sizeChanged = true;
                    }
                }
                using (var check = new EditorGUI.ChangeCheckScope())
                {
                    var newSize = EditorGUILayout.IntPopup(sizeProperty.intValue, sizes.Select(s => s.ToString()).ToArray(), sizes);
                    if (check.changed)
                    {
                        sizeProperty.intValue = newSize;
                        sizeChanged = true;
                    }
                }

                if (sizeChanged)
                {
                    if (sizeProperty.intValue > 2048) sizeProperty.intValue = 2048;
                    var newSize = sizeProperty.intValue;
                    if (renderTexture != null) DestroyImmediate(renderTexture);
                    renderTexture = new RenderTexture(newSize, newSize, 24);
                    camera.targetTexture = renderTexture;
                }
            }

            var width = Mathf.Min(position.width - 10, renderTexture.width);
            var boxRect = EditorGUILayout.GetControlRect(false, renderTexture.height / (float)renderTexture.width * width);
            boxRect.x += (boxRect.width - width) / 2;
            boxRect.width = width;
            GUI.Box(boxRect, renderTexture, new GUIStyle("box"));

            var selectedGameObjects = Selection.gameObjects;
            renderTargets = selectedGameObjects.SelectMany(o => o.GetComponentsInChildren<Renderer>()).Where(r => r.enabled).Select(r => r.gameObject).Distinct().Where(o => o.activeInHierarchy).ToArray();
            
            var firstSelection = selectedGameObjects.FirstOrDefault();
            if (firstSelection == null)
            {
                emulator = null;
            }
            else
            {
                emulator = firstSelection.GetComponent<LyumaAv3Runtime>();
                if (emulator == null) emulator = firstSelection.GetComponentInParent<LyumaAv3Runtime>();
            }
            if (emulator == null)
            {
                EditorGUILayout.HelpBox("Please select a GameObject in active avatar.", MessageType.Info);
                return;
            }

            selectedIntIndexProperty.intValue = EditorGUILayout.IntPopup(selectedIntIndexProperty.intValue, emulator.Ints.Select(p => p.name).ToArray(), emulator.Ints.Select((_, i) => i).ToArray());

            serializedObject.ApplyModifiedProperties();

            using (new EditorGUI.DisabledGroupScope(targetControls != null)) {
                if (GUILayout.Button("Generate"))
                {
                    var newDir = EditorUtility.SaveFolderPanel("Save Icons", dir, "");
                    if (string.IsNullOrEmpty(newDir)) return;
                    dir = newDir;
                    var avatarDescriptor = emulator.GetComponent<VRCAvatarDescriptor>();
                    targetControls = GetAllControls(avatarDescriptor.expressionsMenu.controls, emulator.Ints[selectedIntIndex].name);
                    targetControlIndex = -1;
                    afterParameterSet = int.MaxValue;
                }
            }

            var progressBarRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            if (targetControls == null || targetControlIndex < 0)
            {
                EditorGUI.ProgressBar(progressBarRect, 1, "");
            }
            else
            {
                EditorGUI.ProgressBar(progressBarRect, targetControlIndex / (float)targetControls.Length, $"{targetControlIndex} / {targetControls.Length}");
            }

            if (renderTargets.Length == 0)
            {
                EditorGUILayout.HelpBox("Select renderers!", MessageType.Info);
            }
        }

        void OnSceneGUI(SceneView sceneView)
        {
            if (camera == null) return;
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                var cameraPosition = Handles.PositionHandle(camera.transform.position, Quaternion.identity);
                if (check.changed)
                {
                    camera.transform.position = cameraPosition;
                }
            }
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                var cameraRotation = Handles.RotationHandle(camera.transform.rotation, camera.transform.position);
                if (check.changed)
                {
                    camera.transform.rotation = cameraRotation;
                }
            }
        }

        void Update()
        {
            Repaint();

            if (!EditorApplication.isPlaying || camera == null || renderTargets == null || emulator == null) return;

            var originalLayers = renderTargets.Select(o => o.layer).ToArray();
            foreach (var o in renderTargets) o.layer = layer;
            camera.cullingMask = cullingMask;
            camera.Render();
            camera.cullingMask = -1;
            for (var i = 0; i < renderTargets.Length; i++) renderTargets[i].layer = originalLayers[i];

            if (targetControls != null)
            {
                if (afterParameterSet < int.MaxValue) afterParameterSet++;

                if (afterParameterSet >= 20)
                {
                    targetControlIndex++;
                    if (targetControlIndex < 0 || targetControlIndex >= targetControls.Length)
                    {
                        targetControls = null;
                        return;
                    }
                    var control = targetControls[targetControlIndex];
                    var value = control.value;
                    emulator.Ints[selectedIntIndex].value = Mathf.FloorToInt(value);
                    afterParameterSet = 0;
                }

                if (afterParameterSet >= 10)
                {
                    if (targetControlIndex < 0 || targetControlIndex > targetControls.Length)
                    {
                        return;
                    }
                    var icon = new Texture2D(size, size, TextureFormat.RGBA32, false);
                    var active = RenderTexture.active;
                    RenderTexture.active = renderTexture;
                    icon.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                    icon.Apply();
                    RenderTexture.active = active;

                    var control = targetControls[targetControlIndex];
                    var saveName = control.name;
                    if (useMaterialName)
                    {
                        var renderer = materialNameSource != null && renderTargets.Contains(materialNameSource.gameObject) ? materialNameSource : null;
                        if (renderer == null && renderTargets.Length > 0) renderer = renderTargets.First().GetComponent<Renderer>();
                        if (renderer != null)
                        {
                            var material = renderer.sharedMaterial;
                            if (material != null)
                            {
                                saveName = material.name;
                            }
                        }
                    }
                    var path = System.IO.Path.Join(dir, saveName + ".png");
                    System.IO.File.WriteAllBytes(path, icon.EncodeToPNG());
                    Task.Run(async () =>
                    {
                        await Task.Delay(100);
                        AssetDatabase.ImportAsset(path);
                    });
                }
            }
        }

        VRCExpressionsMenu.Control[] GetAllControls(IEnumerable<VRCExpressionsMenu.Control> controls, string parameterName)
        {
            return controls
                .Where(c => c.parameter.name == parameterName)
                .Concat(
                    controls
                    .Where(c => c.type == VRCExpressionsMenu.Control.ControlType.SubMenu)
                    .SelectMany(c => GetAllControls(c.subMenu.controls, parameterName))
                    )
                .ToArray();
        }
    }
}
