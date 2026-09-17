using System;
using System.IO;
using TexturePackEditor;
using UnityEditor;
using UnityEngine;

// Requires an existing valid local setup. Exercises failure recovery then fresh installation.
public static class DeepBumpSetupChecks
{
    static string python, pointer;
    static int phase;
    static double started;
    public static void Run()
    {
        if (!TexturePackDeepBump.Ready) throw new Exception("Install DeepBump before running setup recovery checks.");
        python = TexturePackDeepBump.Python;
        pointer = ReadPointer();
        TexturePackDeepBump.Python = Path.Combine(TexturePackDeepBump.Root, "missing-python.exe");
        TexturePackDeepBump.Install();
        started = EditorApplication.timeSinceStartup;
        EditorApplication.update += Poll;
    }
    static string ReadPointer()
    {
        string path = Path.Combine(TexturePackDeepBump.Root, "active.txt");
        return File.Exists(path) ? File.ReadAllText(path) : "legacy";
    }
    static void Poll()
    {
        try
        {
            if (EditorApplication.timeSinceStartup - started > 240) throw new Exception("Setup timed out: " + TexturePackDeepBump.Status);
            if (TexturePackDeepBump.Installing) return;
            if (phase == 0)
            {
                if (!TexturePackDeepBump.HasError || !TexturePackDeepBump.Ready || ReadPointer() != pointer)
                    throw new Exception("Failed setup changed the working installation");
                TexturePackDeepBump.Python = python;
                TexturePackDeepBump.Install(); phase = 1; return;
            }
            if (TexturePackDeepBump.HasError || !TexturePackDeepBump.Ready || ReadPointer() == pointer)
                throw new Exception("Fresh installation failed: " + TexturePackDeepBump.Status);
            File.WriteAllText("deepbump-setup-results.txt", "PASS: failed setup preserves installed backend; fresh setup and model self-test succeed; active installation switches atomically");
            EditorApplication.update -= Poll;
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            TexturePackDeepBump.Python = python;
            TexturePackDeepBump.CancelSetup();
            EditorApplication.update -= Poll;
            File.WriteAllText("deepbump-setup-results.txt", e.ToString());
            Debug.LogException(e); EditorApplication.Exit(1);
        }
    }
}
