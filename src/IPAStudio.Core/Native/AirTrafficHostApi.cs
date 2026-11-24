using System.Runtime.InteropServices;
using IPAStudio.Core.Diagnostics;
using Microsoft.Win32;

namespace IPAStudio.Core.Native;

/// <summary>
/// Managed bridge to Apple's native AirTrafficHost library (AirTrafficHost.dll)
/// and CoreFoundation.dll for coordinating device media sync with iOS atc daemon.
/// </summary>
public static class AirTrafficHostApi
{
    private static readonly object InitGate = new();
    private static bool _initialized;
    private static bool _available;

    private static IntPtr _athModule = IntPtr.Zero;
    private static IntPtr _cfModule = IntPtr.Zero;

    // AirTrafficHost delegates
    private static ATHostConnectionCreateWithLibraryDelegate? _createWithLibrary;
    private static ATHostConnectionSendHostInfoDelegate? _sendHostInfo;
    private static ATHostConnectionSendSyncRequestDelegate? _sendSyncRequest;
    private static ATHostConnectionSendMetadataSyncFinishedDelegate? _sendMetadataSyncFinished;
    private static ATHostConnectionDestroyDelegate? _destroy;
    private static ATHostWaitForQueueToDrainDelegate? _waitForQueueToDrain;
    private static ATAddAppleSearchPathsToEnvironmentFromRegDelegate? _addSearchPaths;

    // CoreFoundation delegates
    private static CFStringCreateWithCharactersDelegate? _cfStringCreate;
    private static CFArrayCreateMutableDelegate? _cfArrayCreateMutable;
    private static CFArrayAppendValueDelegate? _cfArrayAppendValue;
    private static CFDictionaryCreateMutableDelegate? _cfDictionaryCreateMutable;
    private static CFDictionarySetValueDelegate? _cfDictionarySetValue;
    private static CFReleaseDelegate? _cfRelease;

    private static IntPtr _kCFTypeArrayCallBacks = IntPtr.Zero;
    private static IntPtr _kCFTypeDictionaryKeyCallBacks = IntPtr.Zero;
    private static IntPtr _kCFTypeDictionaryValueCallBacks = IntPtr.Zero;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ATHostConnectionCreateWithLibraryDelegate(IntPtr libraryName, IntPtr udid, int options);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ATHostConnectionSendHostInfoDelegate(IntPtr connection, IntPtr hostInfoDict);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ATHostConnectionSendSyncRequestDelegate(IntPtr connection, IntPtr dataclassesArray, IntPtr optionsDict, IntPtr hostInfoDict);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ATHostConnectionSendMetadataSyncFinishedDelegate(IntPtr connection, IntPtr syncTypesArray, IntPtr dataclassAnchorsDict);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ATHostConnectionDestroyDelegate(IntPtr connection);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ATHostWaitForQueueToDrainDelegate(IntPtr connection);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ATAddAppleSearchPathsToEnvironmentFromRegDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CFStringCreateWithCharactersDelegate(IntPtr alloc, [MarshalAs(UnmanagedType.LPWStr)] string str, long numChars);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CFArrayCreateMutableDelegate(IntPtr alloc, long capacity, IntPtr callbacks);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CFArrayAppendValueDelegate(IntPtr array, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CFDictionaryCreateMutableDelegate(IntPtr alloc, long capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CFDictionarySetValueDelegate(IntPtr dict, IntPtr key, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CFReleaseDelegate(IntPtr cfType);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string? lpPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string lpPathName);

    /// <summary>
    /// Gets whether AirTrafficHost.dll and CoreFoundation.dll are present and initialized.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _available;
        }
    }

    /// <summary>
    /// Ensures native libraries are loaded and delegates bound.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (InitGate)
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                var athPath = LocateAirTrafficHostDll();
                if (string.IsNullOrWhiteSpace(athPath) || !File.Exists(athPath))
                {
                    AppLog.Info("AirTraffic: AirTrafficHost.dll not found on this system.");
                    return;
                }

                var athDir = Path.GetDirectoryName(athPath);
                if (!string.IsNullOrEmpty(athDir))
                {
                    SetDllDirectory(athDir);
                    try { AddDllDirectory(athDir); } catch { }
                }

                // Locate CoreFoundation.dll
                var cfPath = LocateCoreFoundationDll(athDir);
                if (string.IsNullOrWhiteSpace(cfPath) || !File.Exists(cfPath))
                {
                    AppLog.Info("AirTraffic: CoreFoundation.dll not found on this system.");
                    return;
                }

                var cfDir = Path.GetDirectoryName(cfPath);
                if (!string.IsNullOrEmpty(cfDir))
                {
                    try { AddDllDirectory(cfDir); } catch { }
                }

                // Ensure both directories are present in the PATH environment variable
                try
                {
                    var curPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    var extra = string.Empty;
                    if (!string.IsNullOrEmpty(athDir) && !curPath.Contains(athDir, StringComparison.OrdinalIgnoreCase))
                    {
                        extra += athDir + Path.PathSeparator;
                    }
                    if (!string.IsNullOrEmpty(cfDir) && !curPath.Contains(cfDir, StringComparison.OrdinalIgnoreCase))
                    {
                        extra += cfDir + Path.PathSeparator;
                    }
                    if (extra.Length > 0)
                    {
                        Environment.SetEnvironmentVariable("PATH", extra + curPath);
                    }
                }
                catch { /* best effort */ }

                _cfModule = NativeLibrary.Load(cfPath);
                _athModule = NativeLibrary.Load(athPath);

                // Optional search paths initialization
                if (NativeLibrary.TryGetExport(_athModule, "ATAddAppleSearchPathsToEnvironmentFromReg", out var addPathsPtr))
                {
                    _addSearchPaths = Marshal.GetDelegateForFunctionPointer<ATAddAppleSearchPathsToEnvironmentFromRegDelegate>(addPathsPtr);
                    try { _addSearchPaths(); } catch { /* best effort */ }
                }

                // AirTrafficHost exports
                _createWithLibrary = Marshal.GetDelegateForFunctionPointer<ATHostConnectionCreateWithLibraryDelegate>(
                    NativeLibrary.GetExport(_athModule, "ATHostConnectionCreateWithLibrary"));

                if (NativeLibrary.TryGetExport(_athModule, "ATHostConnectionSendHostInfo", out var hostInfoPtr))
                {
                    _sendHostInfo = Marshal.GetDelegateForFunctionPointer<ATHostConnectionSendHostInfoDelegate>(hostInfoPtr);
                }

                _sendSyncRequest = Marshal.GetDelegateForFunctionPointer<ATHostConnectionSendSyncRequestDelegate>(
                    NativeLibrary.GetExport(_athModule, "ATHostConnectionSendSyncRequest"));
                _sendMetadataSyncFinished = Marshal.GetDelegateForFunctionPointer<ATHostConnectionSendMetadataSyncFinishedDelegate>(
                    NativeLibrary.GetExport(_athModule, "ATHostConnectionSendMetadataSyncFinished"));
                _destroy = Marshal.GetDelegateForFunctionPointer<ATHostConnectionDestroyDelegate>(
                    NativeLibrary.GetExport(_athModule, "ATHostConnectionDestroy"));

                if (NativeLibrary.TryGetExport(_athModule, "ATHostWaitForQueueToDrain", out var drainPtr))
                {
                    _waitForQueueToDrain = Marshal.GetDelegateForFunctionPointer<ATHostWaitForQueueToDrainDelegate>(drainPtr);
                }

                // CoreFoundation exports
                _cfStringCreate = Marshal.GetDelegateForFunctionPointer<CFStringCreateWithCharactersDelegate>(
                    NativeLibrary.GetExport(_cfModule, "CFStringCreateWithCharacters"));
                _cfArrayCreateMutable = Marshal.GetDelegateForFunctionPointer<CFArrayCreateMutableDelegate>(
                    NativeLibrary.GetExport(_cfModule, "CFArrayCreateMutable"));
                _cfArrayAppendValue = Marshal.GetDelegateForFunctionPointer<CFArrayAppendValueDelegate>(
                    NativeLibrary.GetExport(_cfModule, "CFArrayAppendValue"));
                _cfDictionaryCreateMutable = Marshal.GetDelegateForFunctionPointer<CFDictionaryCreateMutableDelegate>(
                    NativeLibrary.GetExport(_cfModule, "CFDictionaryCreateMutable"));

                if (NativeLibrary.TryGetExport(_cfModule, "CFDictionarySetValue", out var setValPtr))
                {
                    _cfDictionarySetValue = Marshal.GetDelegateForFunctionPointer<CFDictionarySetValueDelegate>(setValPtr);
                }

                _cfRelease = Marshal.GetDelegateForFunctionPointer<CFReleaseDelegate>(
                    NativeLibrary.GetExport(_cfModule, "CFRelease"));

                // Global callbacks
                NativeLibrary.TryGetExport(_cfModule, "kCFTypeArrayCallBacks", out _kCFTypeArrayCallBacks);
                NativeLibrary.TryGetExport(_cfModule, "kCFTypeDictionaryKeyCallBacks", out _kCFTypeDictionaryKeyCallBacks);
                NativeLibrary.TryGetExport(_cfModule, "kCFTypeDictionaryValueCallBacks", out _kCFTypeDictionaryValueCallBacks);

                _available = true;
                AppLog.Info($"AirTraffic: native engine loaded successfully from {athPath}");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"AirTraffic: failed to load native engine: {ex.Message}");
                _available = false;
            }
            finally
            {
                SetDllDirectory(null);
            }
        }
    }

    /// <summary>
    /// Executes the native AirTraffic synchronization handshake to notify iOS atc daemon
    /// of newly staged media manifests in Photos.
    /// </summary>
    /// <param name="udid">Device UDID.</param>
    /// <param name="libraryName">Target sync library name, default "Photos".</param>
    /// <param name="libraryId">Sync library UUID.</param>
    /// <returns>True if the sync signals were accepted by AirTrafficHost.</returns>
    public static bool TriggerSync(string udid, string libraryName = "Photos", string? libraryId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(udid);
        if (!IsAvailable)
        {
            AppLog.Warn("AirTraffic: native engine not available for TriggerSync");
            return false;
        }

        IntPtr libStr = IntPtr.Zero;
        IntPtr itunesStr = IntPtr.Zero;
        IntPtr udidStr = IntPtr.Zero;
        IntPtr conn = IntPtr.Zero;
        IntPtr arr = IntPtr.Zero;
        IntPtr options = IntPtr.Zero;
        IntPtr hostInfo = IntPtr.Zero;
        var cfAllocations = new List<IntPtr>();

        try
        {
            libStr = CreateCFString(libraryName);
            itunesStr = CreateCFString("iTunes");
            udidStr = CreateCFString(udid);

            // Attempt 1: Target library name (e.g. "Photos")
            conn = _createWithLibrary!(libStr, udidStr, 0);

            // Attempt 2: Target library without hyphens in UDID (older iOS/AMDS compatibility)
            if (conn == IntPtr.Zero && udid.Contains('-'))
            {
                ReleaseCF(udidStr);
                udidStr = CreateCFString(udid.Replace("-", string.Empty));
                conn = _createWithLibrary!(libStr, udidStr, 0);
            }

            // Attempt 3: "iTunes" host library identifier
            if (conn == IntPtr.Zero)
            {
                conn = _createWithLibrary!(itunesStr, udidStr, 0);
            }

            // Attempt 4: Null library identifier (default host connection)
            if (conn == IntPtr.Zero)
            {
                conn = _createWithLibrary!(IntPtr.Zero, udidStr, 0);
            }

            if (conn == IntPtr.Zero)
            {
                AppLog.Warn($"AirTraffic: ATHostConnectionCreateWithLibrary returned null for device {udid}");
                return false;
            }

            // Create array with sync data classes (e.g. "Photos")
            arr = _cfArrayCreateMutable!(IntPtr.Zero, 0, _kCFTypeArrayCallBacks);
            _cfArrayAppendValue!(arr, libStr);

            // Create options dictionary
            options = _cfDictionaryCreateMutable!(IntPtr.Zero, 0, _kCFTypeDictionaryKeyCallBacks, _kCFTypeDictionaryValueCallBacks);

            // Create and populate HostInfo dictionary for atc daemon handshake
            hostInfo = _cfDictionaryCreateMutable!(IntPtr.Zero, 0, _kCFTypeDictionaryKeyCallBacks, _kCFTypeDictionaryValueCallBacks);
            SetCFDictValue(hostInfo, "Type", "iTunes", cfAllocations);
            SetCFDictValue(hostInfo, "Version", "13.7.0.161", cfAllocations);
            SetCFDictValue(hostInfo, "SyncHostName", Environment.MachineName, cfAllocations);
            SetCFDictValue(hostInfo, "SyncedDataclasses", arr, cfAllocations);
            SetCFDictValue(hostInfo, "LibraryID", libraryId ?? Guid.NewGuid().ToString().ToUpperInvariant(), cfAllocations);

            // Step 1: Send HostInfo to initialize AirTraffic connection state machine
            int hostInfoRes = 0;
            if (_sendHostInfo != null)
            {
                try
                {
                    hostInfoRes = _sendHostInfo(conn, hostInfo);
                }
                catch (Exception ex)
                {
                    AppLog.Info($"AirTraffic: sendHostInfo notice: {ex.Message}");
                }
            }

            // Step 2: Send sync request for dataclasses
            var reqRes = _sendSyncRequest!(conn, arr, options, hostInfo);

            // Step 3: Send metadata sync finished with dataclass array and anchors
            var finRes = _sendMetadataSyncFinished!(conn, arr, options);

            AppLog.Info($"AirTraffic: sync signaled for {udid} (hostInfo={hostInfoRes}, req={reqRes}, fin={finRes})");

            // Step 4: Drain queued async operations to ensure messages flush across usbmux
            if (_waitForQueueToDrain != null)
            {
                try { _waitForQueueToDrain(conn); } catch { }
            }
            Thread.Sleep(300);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"AirTraffic: exception triggering sync for {udid}: {ex.Message}");
            return false;
        }
        finally
        {
            if (conn != IntPtr.Zero)
            {
                try { _destroy!(conn); } catch { /* best effort */ }
            }
            foreach (var cfPtr in cfAllocations)
            {
                ReleaseCF(cfPtr);
            }
            ReleaseCF(arr);
            ReleaseCF(options);
            ReleaseCF(hostInfo);
            ReleaseCF(libStr);
            ReleaseCF(itunesStr);
            ReleaseCF(udidStr);
        }
    }

    private static void SetCFDictValue(IntPtr dict, string key, string value, List<IntPtr> toRelease)
    {
        if (dict == IntPtr.Zero || _cfDictionarySetValue == null) return;
        var keyRef = CreateCFString(key);
        var valRef = CreateCFString(value);
        if (keyRef != IntPtr.Zero) toRelease.Add(keyRef);
        if (valRef != IntPtr.Zero) toRelease.Add(valRef);
        if (keyRef != IntPtr.Zero && valRef != IntPtr.Zero)
        {
            _cfDictionarySetValue(dict, keyRef, valRef);
        }
    }

    private static void SetCFDictValue(IntPtr dict, string key, IntPtr value, List<IntPtr> toRelease)
    {
        if (dict == IntPtr.Zero || value == IntPtr.Zero || _cfDictionarySetValue == null) return;
        var keyRef = CreateCFString(key);
        if (keyRef != IntPtr.Zero)
        {
            toRelease.Add(keyRef);
            _cfDictionarySetValue(dict, keyRef, value);
        }
    }

    private static IntPtr CreateCFString(string text)
    {
        if (_cfStringCreate == null) return IntPtr.Zero;
        return _cfStringCreate(IntPtr.Zero, text, text.Length);
    }

    private static void ReleaseCF(IntPtr handle)
    {
        if (handle != IntPtr.Zero && _cfRelease != null)
        {
            try { _cfRelease(handle); } catch { /* best effort */ }
        }
    }

    private static string? LocateAirTrafficHostDll()
    {
        if (OperatingSystem.IsWindows())
        {
            string[] subKeys =
            {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Apple Inc.\Apple Mobile Device Support\Shared",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Apple Inc.\Apple Mobile Device Support\Shared",
            };

            foreach (var key in subKeys)
            {
                try
                {
                    var reg = Registry.GetValue(key, "AirTrafficHostDLL", null) as string;
                    if (!string.IsNullOrWhiteSpace(reg) && File.Exists(reg)) return reg;
                }
                catch { }
            }
        }

        string[] candidates =
        {
            @"C:\Program Files\Common Files\Apple\Mobile Device Support\AirTrafficHost.dll",
            @"C:\Program Files (x86)\Common Files\Apple\Mobile Device Support\AirTrafficHost.dll",
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? LocateCoreFoundationDll(string? athDir)
    {
        if (!string.IsNullOrEmpty(athDir))
        {
            var direct = Path.Combine(athDir, "CoreFoundation.dll");
            if (File.Exists(direct)) return direct;
        }

        if (OperatingSystem.IsWindows())
        {
            string[] aasKeys =
            {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Apple Inc.\Apple Application Support",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Apple Inc.\Apple Application Support",
            };

            foreach (var key in aasKeys)
            {
                try
                {
                    var installDir = Registry.GetValue(key, "InstallDir", null) as string;
                    if (!string.IsNullOrWhiteSpace(installDir))
                    {
                        var cf = Path.Combine(installDir, "CoreFoundation.dll");
                        if (File.Exists(cf)) return cf;
                    }
                }
                catch { }
            }
        }

        string[] candidates =
        {
            @"C:\Program Files\Common Files\Apple\Apple Application Support\CoreFoundation.dll",
            @"C:\Program Files (x86)\Common Files\Apple\Apple Application Support\CoreFoundation.dll",
            @"C:\Program Files\Common Files\Apple\Mobile Device Support\CoreFoundation.dll",
            @"C:\Program Files (x86)\Common Files\Apple\Mobile Device Support\CoreFoundation.dll",
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
