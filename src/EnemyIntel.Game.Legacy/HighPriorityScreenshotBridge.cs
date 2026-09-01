using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;

namespace EnemyIntel.Game.Legacy;

public sealed record ScreenshotCaptureResult(bool Succeeded, bool Canceled, string? AppliedPath, string Message);

public static class HighPriorityScreenshotBridge
{
    public static ScreenshotCaptureResult CaptureAndApply(Window owner, int paramId, string captureDirectory)
    {
        if (paramId <= 0) return new(false, false, null, "Lock onto an enemy first.");

        using var process = Process.GetCurrentProcess();
        var originalProcessPriority = process.PriorityClass;
        var originalThreadPriority = Thread.CurrentThread.Priority;
        try
        {
            TrySetProcessPriority(process, ProcessPriorityClass.High);
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;

            var capturePath = global::EnemyIntelReader.ScreenshotRegionWindow.Capture(owner, captureDirectory, out var error);
            if (!string.IsNullOrWhiteSpace(error)) return new(false, false, null, error);
            if (string.IsNullOrWhiteSpace(capturePath)) return new(false, true, null, "Capture canceled.");

            var characterDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "Characters");
            Directory.CreateDirectory(characterDirectory);
            var appliedPath = Path.Combine(characterDirectory, $"{paramId}.png");
            File.Copy(capturePath, appliedPath, true);
            return new(true, false, appliedPath, $"Portrait updated for Param {paramId}.");
        }
        catch (Exception exception)
        {
            return new(false, false, null, exception.Message);
        }
        finally
        {
            Thread.CurrentThread.Priority = originalThreadPriority;
            TrySetProcessPriority(process, originalProcessPriority);
        }
    }

    private static void TrySetProcessPriority(Process process, ProcessPriorityClass priority)
    {
        try { process.PriorityClass = priority; }
        catch { /* Priority elevation is an optimization; capture remains functional without it. */ }
    }
}
