using System;
using System.Net;
using System.Runtime.InteropServices;
using ContinuityHub.Core;

namespace ContinuityHub.Platform;

/// <summary>
/// Advertises _continuity._tcp.local via the built-in Windows DNS-SD responder (dnsapi!DnsServiceRegister,
/// Windows 10 1903+). No third-party mDNS stack, and no fight over UDP 5353.
/// </summary>
internal sealed class DnsSdAdvertiser : IDisposable
{
    private const uint DNS_REQUEST_PENDING = 9506;
    private const uint DNS_QUERY_REQUEST_VERSION1 = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DNS_SERVICE_REGISTER_REQUEST
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr pServiceInstance;
        public IntPtr pRegisterCompletionCallback;
        public IntPtr pQueryContext;
        public IntPtr hCredentials;
        public int unicastEnabled;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RegisterComplete(uint status, IntPtr context, IntPtr instance);

    private readonly RegisterComplete _callback; // kept alive for the native side
    private IntPtr _instance;
    private IntPtr _request;

    public DnsSdAdvertiser() => _callback = OnComplete;

    public void Register(string instanceName, IPAddress ipv4, int port, string hubId, string fingerprint)
    {
        Unregister();
        var safeName = instanceName.Replace(".", "-");
        var serviceName = $"{safeName}._continuity._tcp.local";
        var hostName = $"{Environment.MachineName}.local";
        uint ip = BitConverter.ToUInt32(ipv4.GetAddressBytes(), 0); // network byte order in memory

        string[] keys = { "v", "id", "fp" };
        string[] values = { "1", hubId, fingerprint[..16] };
        _instance = DnsServiceConstructInstance(serviceName, hostName, ref ip, IntPtr.Zero, (ushort)port, 0, 0,
            (uint)keys.Length, keys, values);
        if (_instance == IntPtr.Zero) { Log.Warn("DnsServiceConstructInstance failed"); return; }

        var req = new DNS_SERVICE_REGISTER_REQUEST
        {
            Version = DNS_QUERY_REQUEST_VERSION1,
            InterfaceIndex = 0,
            pServiceInstance = _instance,
            pRegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(_callback),
        };
        _request = Marshal.AllocHGlobal(Marshal.SizeOf<DNS_SERVICE_REGISTER_REQUEST>());
        Marshal.StructureToPtr(req, _request, false);

        var rc = DnsServiceRegister(_request, IntPtr.Zero);
        if (rc != DNS_REQUEST_PENDING) Log.Warn($"DnsServiceRegister returned {rc}");
        else Log.Info($"mDNS: advertising {serviceName} -> {ipv4}:{port}");
    }

    private void OnComplete(uint status, IntPtr context, IntPtr instance)
    {
        if (status != 0) Log.Warn($"mDNS registration callback status {status}");
        if (instance != IntPtr.Zero) DnsServiceFreeInstance(instance);
    }

    public void Unregister()
    {
        if (_request != IntPtr.Zero)
        {
            try { DnsServiceDeRegister(_request, IntPtr.Zero); } catch { }
            // The request block must stay valid until the async deregistration completes; leak it (a few bytes, once).
            _request = IntPtr.Zero;
        }
        _instance = IntPtr.Zero; // freed by DnsServiceDeRegister's completion path / process exit
    }

    public void Dispose() => Unregister();

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DnsServiceConstructInstance(string pServiceName, string pHostName, ref uint pIp4,
        IntPtr pIp6, ushort wPort, ushort wPriority, ushort wWeight, uint dwPropertiesCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] keys,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] values);

    [DllImport("dnsapi.dll")] private static extern uint DnsServiceRegister(IntPtr pRequest, IntPtr pCancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceDeRegister(IntPtr pRequest, IntPtr pCancel);
    [DllImport("dnsapi.dll")] private static extern void DnsServiceFreeInstance(IntPtr pInstance);
}
