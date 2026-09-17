using System;
using System.IO;
using System.Reflection;
using TexturePackEditor;
using UnityEditor;
using UnityEngine;

public static class DeepBumpRecoveryChecks
{
    static double started;
    public static void Run()
    {
        try
        {
            string root = Path.GetFullPath("Library/RecoveryChecks/" + Guid.NewGuid().ToString("N"));
            string active = Path.Combine(root, Guid.NewGuid().ToString("N"));
            string leased = Path.Combine(root, Guid.NewGuid().ToString("N"));
            string unused = Path.Combine(root, Guid.NewGuid().ToString("N"));
            string unrelated = Path.Combine(root, "keep-me");
            foreach (string path in new[] { active, leased, unused, unrelated }) Directory.CreateDirectory(path);
            var config = new TexturePackDeepBump.Configuration { root = leased };
            typeof(TexturePackDeepBump).GetMethod("Lease", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { config });
            if (TexturePackDeepBump.CleanupDirectories(root, active) != 1 || !Directory.Exists(leased) || !Directory.Exists(active) || !Directory.Exists(unrelated))
                throw new Exception("Cleanup failed to protect installed/in-flight/unrelated directories");
            config.Dispose();
            if (TexturePackDeepBump.CleanupDirectories(root, active) != 1 || Directory.Exists(leased)) throw new Exception("Released runtime not cleaned");
            File.WriteAllText(Path.Combine(active, "ready.txt"), "ready");
            File.WriteAllText(Path.Combine(active, "python.txt"), Path.Combine(active, "missing-python.exe"));
            File.WriteAllText(Path.Combine(active, "deepbump256.onnx"), "model");
            if (TexturePackDeepBump.InstallationPresent(active)) throw new Exception("Missing Python was reported ready");
            if (!TexturePackDeepBump.Ready) throw new Exception("A working installation is needed for the health check");
            TexturePackDeepBump.CheckInstallation(true);
            started = EditorApplication.timeSinceStartup;
            EditorApplication.update += Poll;
        }
        catch (Exception e) { Finish(e); }
    }
    static void Poll()
    {
        if (TexturePackDeepBump.Checking && EditorApplication.timeSinceStartup - started < 60) return;
        Finish(TexturePackDeepBump.Healthy ? null : new Exception("Health check failed: " + TexturePackDeepBump.HealthError));
    }
    static void Finish(Exception error)
    {
        EditorApplication.update -= Poll;
        File.WriteAllText("recovery-results.txt", error == null
            ? "PASS: cleanup protects active/leased/unrelated folders, releases unused runtime; missing Python not ready; real model health check passes"
            : error.ToString());
        if (error != null) Debug.LogException(error);
        EditorApplication.Exit(error == null ? 0 : 1);
    }
}
