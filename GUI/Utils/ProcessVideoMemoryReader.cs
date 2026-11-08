using System.Runtime.InteropServices;
using ValveResourceFormat.Renderer;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;

namespace GUI.Utils;

/// <summary>
/// Reads the video memory Windows has charged to this process, the figure Task Manager shows. It
/// counts every allocation the OpenGL driver makes, including the ones the renderer does not track.
/// </summary>
internal static class ProcessVideoMemoryReader
{
    private static List<IDXGIAdapter3>? adapters;

    public static ProcessVideoMemory? Read()
    {
        try
        {
            adapters ??= EnumerateAdapters();
        }
        catch (COMException)
        {
            adapters = [];
        }

        ProcessVideoMemory? result = null;

        // The renderer's adapter is not known from OpenGL, but it is the one holding the most memory
        foreach (var adapter in adapters)
        {
            adapter.QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_LOCAL, out var local);
            adapter.QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, out var nonLocal);

            if (result == null || (long)local.CurrentUsage > result.Value.Dedicated)
            {
                result = new ProcessVideoMemory((long)local.CurrentUsage, (long)local.Budget, (long)nonLocal.CurrentUsage);
            }
        }

        return result;
    }

    private static List<IDXGIAdapter3> EnumerateAdapters()
    {
        PInvoke.CreateDXGIFactory1<IDXGIFactory1>(out var factory).ThrowOnFailure();
        var result = new List<IDXGIAdapter3>();

        for (var i = 0u; factory.EnumAdapters1(i, out var adapter) != HRESULT.DXGI_ERROR_NOT_FOUND; i++)
        {
            // Windows 10 and later implement it on every adapter
            if (adapter is IDXGIAdapter3 adapter3)
            {
                result.Add(adapter3);
            }
        }

        return result;
    }
}
