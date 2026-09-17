using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace TexturePackEditor
{
    [InitializeOnLoad]
    public static class TexturePackDeepBump
    {
        internal sealed class Configuration : IDisposable
        {
            internal string root, python, script;
            internal bool leased;
            public void Dispose()
            {
                lock (InstallationLock)
                {
                    if (!leased) return;
                    leased = false;
                    if (--Leases[root] == 0) Leases.Remove(root);
                }
            }
        }

        private static readonly object InstallationLock = new();
        private static readonly Dictionary<string, int> Leases = new(StringComparer.OrdinalIgnoreCase);

        private static readonly CancellationTokenSource Lifetime = new();
        private static CancellationTokenSource setupCancellation;
        private static Task setupTask;
        private static Task healthTask, cleanupTask;
        private static volatile string healthRoot;
        public static volatile string HealthError;
        private static volatile bool healthVerified;
        public static bool Checking => healthTask != null && !healthTask.IsCompleted;
        public static bool Cleaning => cleanupTask != null && !cleanupTask.IsCompleted;
        public static bool Healthy => Ready && healthRoot == ActiveRoot && healthVerified;
        public static volatile string Status;
        public static volatile bool HasError;
        public static bool Installing => setupTask != null && !setupTask.IsCompleted;
        public static string Root => Path.GetFullPath("Library/TexturePackEditor/DeepBump");
        private static string ActiveRoot
        {
            get
            {
                string pointer = Path.Combine(Root, "active.txt");
                if (!File.Exists(pointer)) return Root;
                string id = File.ReadAllText(pointer).Trim();
                if (!Guid.TryParseExact(id, "N", out _)) return Path.Combine(Root, "invalid-installation");
                return Path.Combine(Root, "installations", id);
            }
        }
        public static bool Ready
            => InstallationPresent(ActiveRoot);

        internal static bool InstallationPresent(string root)
        {
                try
                {
                    return File.Exists(Path.Combine(root, "ready.txt")) && File.Exists(Path.Combine(root, "python.txt")) &&
                        File.Exists(File.ReadAllText(Path.Combine(root, "python.txt")).Trim()) &&
                        File.Exists(Path.Combine(root, "packages", "onnxruntime", "__init__.py")) &&
                        File.Exists(Path.Combine(root, "packages", "numpy", "__init__.py")) &&
                        File.Exists(Path.Combine(root, "deepbump256.onnx"));
                }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
        }
        public static string Python
        {
            get => EditorPrefs.GetString("TexturePackDeepBump.Python:" + Root,
                File.Exists(Path.Combine(ActiveRoot, "python.txt")) ? File.ReadAllText(Path.Combine(ActiveRoot, "python.txt")).Trim() : "python");
            set => EditorPrefs.SetString("TexturePackDeepBump.Python:" + Root, value);
        }

        static TexturePackDeepBump()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
        }

        private static void Stop() { Lifetime.Cancel(); setupCancellation?.Cancel(); }
        public static void CancelSetup() => setupCancellation?.Cancel();

        internal static Configuration Capture(bool requireReady = true)
        {
            if (requireReady && !Ready) throw new InvalidOperationException("DeepBump is not set up. Use Install DeepBump in the Height node.");
            string source = AssetDatabase.FindAssets("TexturePackDeepBump t:MonoScript")
                .Select(AssetDatabase.GUIDToAssetPath).FirstOrDefault(path => Path.GetFileName(path) == "TexturePackDeepBump.cs");
            if (source == null) throw new InvalidOperationException("Cannot locate the DeepBump worker.");
            string script = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source), "TexturePackDeepBump.py"));
            if (!File.Exists(script)) throw new InvalidOperationException("The DeepBump Python worker is missing.");
            lock (InstallationLock)
            {
                var config = new Configuration { root = requireReady ? ActiveRoot : Root,
                    python = requireReady ? File.ReadAllText(Path.Combine(ActiveRoot, "python.txt")).Trim() : Python, script = script };
                if (requireReady) Lease(config);
                return config;
            }
        }

        private static void Lease(Configuration config)
        {
            lock (InstallationLock)
            {
                Leases.TryGetValue(config.root, out int count);
                Leases[config.root] = count + 1;
                config.leased = true;
            }
        }

        public static void CheckInstallation(bool force = false)
        {
            if (Installing || Checking || !Ready || !force && healthRoot == ActiveRoot) return;
            Configuration config = Capture();
            healthRoot = config.root;
            healthVerified = false;
            HealthError = null;
            healthTask = Task.Run(() =>
            {
                using (config)
                {
                    try
                    {
                        Run(config.python, new[] { "-I", config.script, "--root", config.root, "--check" }, Lifetime.Token);
                        healthVerified = true;
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception e) { HealthError = e.Message; }
                }
            });
        }

        public static void CleanupUnusedInstallations()
        {
            if (Installing || Cleaning) return;
            string directory = Path.Combine(Root, "installations");
            cleanupTask = Task.Run(() =>
            {
                try
                {
                    int removed = CleanupDirectories(directory, ActiveRoot);
                    Status = "Removed " + removed + " unused installation(s).";
                    HasError = false;
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { Status = e.Message; HasError = true; }
            });
        }

        internal static int CleanupDirectories(string directory, string active)
        {
            int removed = 0;
            if (!Directory.Exists(directory)) return removed;
            foreach (string path in Directory.GetDirectories(directory))
            {
                Lifetime.Token.ThrowIfCancellationRequested();
                // Only installer-owned GUID directories; never follow links or delete an active job's runtime.
                if (!Guid.TryParseExact(Path.GetFileName(path), "N", out _) ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                lock (InstallationLock)
                {
                    if (string.Equals(path, active, StringComparison.OrdinalIgnoreCase) || Leases.ContainsKey(path)) continue;
                    Directory.Delete(path, true);
                    removed++;
                }
            }
            return removed;
        }

        public static void Install()
        {
            if (Installing || Checking || Cleaning) return;
            HasError = false;
            Configuration configuration = Capture(false);
            string root = configuration.root;
            string installationId = Guid.NewGuid().ToString("N");
            configuration.root = Path.Combine(root, "installations", installationId);
            setupCancellation?.Dispose();
            setupCancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            CancellationToken token = setupCancellation.Token;
            setupTask = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(configuration.root);
                    Status = "Installing local dependencies…";
                    Run(configuration.python, new[] { "-I", "-m", "pip", "install", "--disable-pip-version-check", "--only-binary=:all:",
                        "--target", Path.Combine(configuration.root, "packages"), "numpy==2.2.6", "onnxruntime==1.22.1" }, token);
                    Status = "Downloading and checking the model…";
                    Run(configuration.python, new[] { "-I", configuration.script, "--root", configuration.root, "--install-model" }, token);
                    token.ThrowIfCancellationRequested();
                    string pointer = Path.Combine(root, "active.txt");
                    string temporary = Path.Combine(root, installationId + ".ready");
                    File.WriteAllText(temporary, installationId);
                    if (File.Exists(pointer)) File.Replace(temporary, pointer, null);
                    else File.Move(temporary, pointer);
                    healthRoot = configuration.root;
                    healthVerified = true;
                    HealthError = null;
                    Status = "DeepBump ready.";
                }
                catch (OperationCanceledException) { Status = "DeepBump setup cancelled."; }
                catch (Exception exception) { Status = exception.Message; HasError = true; }
            }, token);
        }

        internal static Color32[] Infer(TexturePackHeight.Input input, bool seamless, CancellationToken token)
        {
            Configuration configuration = input.backend ?? throw new InvalidOperationException("DeepBump configuration was not captured.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, Lifetime.Token);
            token = linked.Token;
            string jobs = Path.Combine(configuration.root, "jobs");
            Directory.CreateDirectory(jobs);
            string prefix = Path.Combine(jobs, Guid.NewGuid().ToString("N"));
            string source = prefix + ".input", destination = prefix + ".output";
            try
            {
                var bytes = new byte[checked(input.pixels.Length * 4)];
                for (int i = 0; i < input.pixels.Length; i++)
                {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    var pixel = input.pixels[i];
                    bytes[i * 4] = pixel.r; bytes[i * 4 + 1] = pixel.g; bytes[i * 4 + 2] = pixel.b; bytes[i * 4 + 3] = pixel.a;
                }
                File.WriteAllBytes(source, bytes);
                Run(configuration.python, new[] { "-I", configuration.script, "--root", configuration.root, "--input", source,
                    "--output", destination, "--width", input.width.ToString(), "--height", input.height.ToString(),
                    "--seamless", seamless ? "1" : "0" }, token);
                bytes = File.ReadAllBytes(destination);
                if (bytes.Length != input.pixels.Length * 4) throw new InvalidDataException("DeepBump returned an incomplete normal map.");
                var result = new Color32[input.pixels.Length];
                for (int i = 0; i < result.Length; i++) result[i] = new Color32(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2], 255);
                return result;
            }
            finally
            {
                if (File.Exists(source)) File.Delete(source);
                if (File.Exists(destination)) File.Delete(destination);
            }
        }

        private static void Run(string executable, string[] arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(executable, string.Join(" ", arguments.Select(Quote)))
                {
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = true, RedirectStandardOutput = true
                }
            };
            if (!process.Start()) throw new InvalidOperationException("Could not start Python. Choose a Python 3.10–3.13 executable.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
            try
            {
                while (!process.WaitForExit(100)) token.ThrowIfCancellationRequested();
                token.ThrowIfCancellationRequested();
                string error = stderr.GetAwaiter().GetResult();
                string output = stdout.GetAwaiter().GetResult();
                if (process.ExitCode != 0)
                {
                    string message = string.IsNullOrWhiteSpace(error) ? output : error;
                    throw new InvalidOperationException("DeepBump: " + message.Trim());
                }
            }
            finally
            {
                if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
            }
        }

        // ProcessStartInfo.Arguments quoting, including trailing backslashes on Windows.
        private static string Quote(string value)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                result.Append(c);
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            return result.Append('"').ToString();
        }
    }
}
