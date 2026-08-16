using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer;

namespace RacinageFreeShareTarget;

internal static class Program {
  [DllImport("user32.dll")]
  private static extern bool SetForegroundWindow(IntPtr window);

  [STAThread]
  private static async Task Main() {
    ShareOperation? operation = null;
    try {
      AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
      if (activation.Kind != ExtendedActivationKind.ShareTarget || activation.Data is not ShareTargetActivatedEventArgs shared) return;
      operation = shared.ShareOperation;
      operation.ReportStarted();
      string value = await ReadSupportedValue(operation.Data);
      if (String.IsNullOrWhiteSpace(value) || value.Length > 32768) {
        operation.ReportError("Racinage Free accepts a link or text of 32 KB or fewer.");
        return;
      }
      operation.ReportDataRetrieved();
      WriteReceipt(value.Trim());
      OpenOrWakeRacinageFree();
      operation.ReportCompleted();
    } catch (Exception error) {
      try { operation?.ReportError("Racinage Free could not receive this item: " + error.Message); } catch { }
    }
  }

  private static async Task<string> ReadSupportedValue(DataPackageView data) {
    if (data.Contains(StandardDataFormats.WebLink)) {
      Uri link = await data.GetWebLinkAsync();
      if (link.Scheme == Uri.UriSchemeHttp || link.Scheme == Uri.UriSchemeHttps) return link.AbsoluteUri;
    }
    if (data.Contains(StandardDataFormats.Text)) return await data.GetTextAsync();
    throw new InvalidDataException("Only URI and text shares are supported.");
  }

  private static void WriteReceipt(string value) {
    string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Racinage Free");
    string inbox = Path.Combine(root, "data", "share-inbox");
    Directory.CreateDirectory(inbox);
    string id = Guid.NewGuid().ToString("N"), destination = Path.Combine(inbox, id + ".json"), temporary = destination + ".tmp";
    byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { value, received_at = DateTime.UtcNow.ToString("O") });
    using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
      stream.Write(payload);
      stream.Flush(true);
    }
    File.Move(temporary, destination);
  }

  private static void OpenOrWakeRacinageFree() {
    Process? running = Process.GetProcessesByName("RacinageFreeHost").FirstOrDefault();
    if (running != null) {
      try { if (running.MainWindowHandle != IntPtr.Zero) SetForegroundWindow(running.MainWindowHandle); } finally { running.Dispose(); }
      return;
    }
    string launcher = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Racinage Free", "RacinageFree.exe");
    if (!File.Exists(launcher)) throw new FileNotFoundException("Open Racinage Free once before enabling its Windows Share Target.");
    Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = true });
  }
}
