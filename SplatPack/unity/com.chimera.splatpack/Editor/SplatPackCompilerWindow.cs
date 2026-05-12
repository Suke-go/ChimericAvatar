using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace SplatPack.Editor
{
    public sealed class SplatPackCompilerWindow : EditorWindow
    {
        private string compilerPath = "SplatPackCompiler.exe";
        private string inputPlyPath = string.Empty;
        private string outputPackagePath = "Assets/StreamingAssets/output.splatpack";
        private int chunkSize = 4096;

        [MenuItem("Tools/SplatPack/Compiler")]
        public static void Open()
        {
            GetWindow<SplatPackCompilerWindow>("SplatPack Compiler");
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Compiler", EditorStyles.boldLabel);
            compilerPath = EditorGUILayout.TextField("Compiler Path", compilerPath);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Input", EditorStyles.boldLabel);
            inputPlyPath = EditorGUILayout.TextField("Input .ply", inputPlyPath);
            outputPackagePath = EditorGUILayout.TextField("Output .splatpack", outputPackagePath);
            chunkSize = EditorGUILayout.IntField("Chunk Size", Mathf.Max(1, chunkSize));

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(compilerPath)
                                               || string.IsNullOrWhiteSpace(inputPlyPath)
                                               || string.IsNullOrWhiteSpace(outputPackagePath)))
            {
                if (GUILayout.Button("Compile"))
                {
                    Compile();
                }
            }
        }

        private void Compile()
        {
            string outputFullPath = Path.GetFullPath(outputPackagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath) ?? ".");

            var startInfo = new ProcessStartInfo
            {
                FileName = compilerPath,
                Arguments = Quote(inputPlyPath) + " " + Quote(outputFullPath) + " --chunk-size " + chunkSize,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            Process process = Process.Start(startInfo);
            if (process == null)
            {
                UnityEngine.Debug.LogError("Failed to start SplatPack compiler.");
                return;
            }

            try
            {
                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    UnityEngine.Debug.Log(stdout);
                }

                if (process.ExitCode != 0)
                {
                    UnityEngine.Debug.LogError(stderr);
                    EditorUtility.DisplayDialog("SplatPack compile failed", stderr, "OK");
                    return;
                }

                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("SplatPack compile complete", outputFullPath, "OK");
            }
            finally
            {
                process.Dispose();
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
