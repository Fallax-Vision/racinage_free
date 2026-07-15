using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace RacinageFreeBootstrap {
  internal static class Program {
    private const string Version = "0.13.2";
    private const string AppName = "Racinage Free";
    private const string HostExe = "RacinageFreeHost.exe";
    private const string LauncherExe = "RacinageFree.exe";
    private const string PayloadResource = "RacinageFree.Payload.zip";

    [STAThread]
    private static void Main() {
      try {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
        string application = Path.Combine(root, "app-v" + Version);
        string installing = application + ".installing";
        string executable = Path.Combine(application, HostExe);
        string launcher = Path.Combine(root, LauncherExe);
        string stamp = Path.Combine(application, ".payload-hash");

        EnsureMutableFolders(root);
        bool launchedFromLauncher = PathsEqual(Application.ExecutablePath, launcher);
        bool wasRunning = CloseRunningApp(root);
        if (!launchedFromLauncher) CopyFileWithRetry(Application.ExecutablePath, launcher, root);

        byte[] payloadBytes;
        using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource)) {
          if (payload == null) throw new InvalidOperationException("The Racinage Free application payload is missing.");
          using (MemoryStream buffer = new MemoryStream()) {
            payload.CopyTo(buffer);
            payloadBytes = buffer.ToArray();
          }
        }

        string payloadHash = ComputeHash(payloadBytes);
        bool installed = File.Exists(executable);
        string installedHash = installed && File.Exists(stamp) ? File.ReadAllText(stamp).Trim() : "";
        if (!installed || installedHash != payloadHash) {
          if (Directory.Exists(installing)) DeleteDirectoryWithRetry(installing, root);
          Directory.CreateDirectory(installing);
          using (MemoryStream source = new MemoryStream(payloadBytes))
          using (ZipArchive archive = new ZipArchive(source, ZipArchiveMode.Read)) {
            archive.ExtractToDirectory(installing);
          }
          if (!File.Exists(Path.Combine(installing, HostExe))) {
            throw new InvalidOperationException("The Racinage Free payload is incomplete.");
          }
          if (Directory.Exists(application)) DeleteDirectoryWithRetry(application, root);
          Directory.Move(installing, application);
          File.WriteAllText(stamp, payloadHash);
          ClearWebViewCache(root);
        }

        RemoveOtherVersions(root, application);
        CreateShortcuts(launcher, root);
        if (launchedFromLauncher || wasRunning) {
          Process.Start(new ProcessStartInfo(executable) {
            WorkingDirectory = application,
            UseShellExecute = true
          });
        }
      } catch (Exception error) {
        MessageBox.Show(
          "Racinage Free could not prepare its local files.\r\n\r\n" + error.Message,
          "Racinage Free",
          MessageBoxButtons.OK,
          MessageBoxIcon.Error);
      }
    }

    private static void EnsureMutableFolders(string root) {
      Directory.CreateDirectory(root);
      foreach (string name in new[] { "data", "media", "logs", "updates", "device-tokens", "webview" }) {
        Directory.CreateDirectory(Path.Combine(root, name));
      }
    }

    private static void CopyFileWithRetry(string source, string destination, string root) {
      for (int attempt = 0; attempt < 6; attempt++) {
        try {
          Directory.CreateDirectory(Path.GetDirectoryName(destination));
          File.Copy(source, destination, true);
          return;
        } catch (Exception error) {
          if (attempt == 5 || !IsLockError(error)) throw;
          CloseRunningApp(root);
          Thread.Sleep(700);
        }
      }
    }

    private static void DeleteDirectoryWithRetry(string directory, string root) {
      for (int attempt = 0; attempt < 6; attempt++) {
        try {
          if (Directory.Exists(directory)) Directory.Delete(directory, true);
          return;
        } catch (Exception error) {
          if (attempt == 5 || !IsLockError(error)) throw;
          CloseRunningApp(root);
          Thread.Sleep(700);
        }
      }
    }

    private static bool IsLockError(Exception error) {
      return error is IOException || error is UnauthorizedAccessException;
    }

    private static bool CloseRunningApp(string root) {
      bool found = false;
      int currentId = Process.GetCurrentProcess().Id;
      foreach (Process process in Process.GetProcessesByName("RacinageFreeHost")) {
        using (process) {
          if (process.Id == currentId) continue;
          string path = GetProcessPath(process);
          if (path == "" || !IsUnder(path, root)) continue;
          found = true;
          try {
            if (process.MainWindowHandle != IntPtr.Zero) process.CloseMainWindow();
          } catch {
          }
          try {
            if (!process.WaitForExit(8000)) process.Kill();
            process.WaitForExit(3000);
          } catch {
          }
        }
      }
      return found;
    }

    private static string GetProcessPath(Process process) {
      try {
        return process.MainModule.FileName;
      } catch {
        return "";
      }
    }

    private static bool IsUnder(string path, string root) {
      try {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
      } catch {
        return false;
      }
    }

    private static void ClearWebViewCache(string root) {
      string webview = Path.Combine(root, "webview");
      if (!Directory.Exists(webview)) return;
      string[] cachePaths = {
        "Cache",
        "Code Cache",
        "GPUCache",
        "DawnCache",
        "blob_storage",
        Path.Combine("Service Worker", "CacheStorage"),
        Path.Combine("Service Worker", "ScriptCache")
      };
      foreach (string relative in cachePaths) {
        string path = Path.Combine(webview, relative);
        try {
          if (Directory.Exists(path)) Directory.Delete(path, true);
          else if (File.Exists(path)) File.Delete(path);
        } catch {
        }
      }
    }

    private static void RemoveOtherVersions(string root, string current) {
      try {
        foreach (string directory in Directory.GetDirectories(root)) {
          string name = Path.GetFileName(directory);
          bool versioned = name.StartsWith("app-v", StringComparison.OrdinalIgnoreCase);
          bool leftover = name.EndsWith(".installing", StringComparison.OrdinalIgnoreCase);
          if ((versioned && !PathsEqual(directory, current)) || leftover) {
            try { Directory.Delete(directory, true); } catch { }
          }
        }
      } catch {
      }
    }

    private static void CreateShortcuts(string executable, string workingDirectory) {
      string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Racinage");
      Directory.CreateDirectory(startMenu);
      CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Racinage Free.lnk"), executable, workingDirectory);
      CreateShortcut(Path.Combine(startMenu, "Racinage Free.lnk"), executable, workingDirectory);
    }

    private static void CreateShortcut(string path, string executable, string workingDirectory) {
      try {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic shell = Activator.CreateInstance(shellType);
        dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = executable;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = executable + ",0";
        shortcut.Description = "Open Racinage Free";
        shortcut.Save();
      } catch {
      }
    }

    private static bool PathsEqual(string first, string second) {
      return String.Equals(
        Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeHash(byte[] data) {
      using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create()) {
        return BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
      }
    }
  }
}
