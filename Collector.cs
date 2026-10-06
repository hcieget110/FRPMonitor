using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace FRPMonitor;

public sealed record ProcessTraffic(int Pid, string Name, double Upload, double Download);
public sealed record Snapshot(long UploadBytes, long DownloadBytes, long TotalUp, long TotalDown, double Seconds, List<ProcessTraffic> Processes)
{ public List<ConnectionTraffic> Connections { get; init; } = new(); }

public sealed class TrafficAccumulator
{
    private sealed class Counter { public string Name = ""; public long Up, Down; public bool Active; }
    private readonly object gate = new();
    private readonly Dictionary<int, Counter> targets = new();
    private readonly HashSet<string> names;
    private HashSet<int> activePids = new();
    private sealed class ConnectionCounter
    { public int Pid; public string Name = "", Local = "", Remote = "", Protocol = "TCP"; public long Up, Down, TotalUp, TotalDown, LastSeen; public bool Loopback, Included;
      public LinkedListNode<(int Pid, IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, string Protocol)>? Node; }
    private readonly Dictionary<(int Pid, IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, string Protocol), ConnectionCounter> connections = new();
    private readonly LinkedList<(int Pid, IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, string Protocol)> connectionOrder = new();
    private void RemoveConnection((int Pid, IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, string Protocol) key)
    { if (connections.Remove(key, out var connection) && connection.Node != null) connectionOrder.Remove(connection.Node); }
    private readonly ConnectionMode mode;
    private readonly EndpointRule[] rules;
    private readonly bool includeLoopback;
    private long totalUp, totalDown, pendingUp, pendingDown;
    private long lastSampleTimestamp = Stopwatch.GetTimestamp();
    public TrafficAccumulator(IEnumerable<string> processNames, bool loopback, ConnectionMode connectionMode = ConnectionMode.All, string endpoints = "")
    { names = new(processNames, StringComparer.OrdinalIgnoreCase); includeLoopback = loopback; mode = connectionMode; rules = EndpointRule.ParseAll(endpoints); }
    private void PublishTargets() => Volatile.Write(ref activePids, targets.Where(pair => pair.Value.Active).Select(pair => pair.Key).ToHashSet());
    public void StartProcess(int pid, string name)
    {
        name = Path.GetFileNameWithoutExtension(name);
        lock (gate)
        {
            if (!names.Contains(name)) { if (targets.TryGetValue(pid, out var old)) { old.Active = false; PublishTargets(); } return; }
            if (targets.TryGetValue(pid, out var existing) && existing.Active && existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return;
            if (existing != null) { pendingUp += existing.Up; pendingDown += existing.Down; }
            targets[pid] = new() { Name = name, Active = true };
            foreach (var key in connections.Where(pair => pair.Value.Pid == pid).Select(pair => pair.Key).ToArray()) RemoveConnection(key);
            PublishTargets();
        }
    }
    public void StopProcess(int pid) { lock (gate) { if (targets.TryGetValue(pid, out var c)) c.Active = false; PublishTargets(); } }
    public bool IsTarget(int pid) => Volatile.Read(ref activePids).Contains(pid);
    public void RefreshProcesses()
    {
        Dictionary<int, Counter> before;
        lock (gate) before = new(targets);
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { if (names.Contains(p.ProcessName)) { seen.Add(p.Id); StartProcess(p.Id, p.ProcessName); } }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        FinishDiscovery(before, seen);
    }
    private void FinishDiscovery(Dictionary<int, Counter> before, HashSet<int> seen)
    {
        lock (gate)
        {
            foreach (var kv in before)
                if (!seen.Contains(kv.Key) && targets.TryGetValue(kv.Key, out var current) && ReferenceEquals(current, kv.Value)) current.Active = false;
            PublishTargets();
        }
    }
    public void Record(int pid, int bytes, bool upload, IPAddress source, IPAddress destination, int sourcePort = 0, int destinationPort = 0, string protocol = "TCP")
    {
        if (bytes <= 0) return;
        var loopback = IPAddress.IsLoopback(source) || IPAddress.IsLoopback(destination);
        var matched = false;
        if (mode != ConnectionMode.All)
            foreach (var rule in rules) if (rule.Matches(source, sourcePort) || rule.Matches(destination, destinationPort)) { matched = true; break; }
        var included = mode == ConnectionMode.Selected ? matched : mode == ConnectionMode.ExternalAndSelectedLoopback ? !loopback || matched : includeLoopback || !loopback;
        lock (gate)
        {
            if (!targets.TryGetValue(pid, out var c) || !c.Active) return;
            // Bound diagnostic connection storage independently of the selected traffic totals.
            if (sourcePort > 0 && destinationPort > 0)
            {
                var local = upload ? source : destination; var localPort = upload ? sourcePort : destinationPort;
                var remote = upload ? destination : source; var remotePort = upload ? destinationPort : sourcePort;
                var key = (pid, local, localPort, remote, remotePort, protocol);
                if (!connections.TryGetValue(key, out var connection))
                {
                    if (connections.Count >= 2048) RemoveConnection(connectionOrder.First!.Value);
                    connections[key] = connection = new() { Pid = pid, Name = c.Name, Local = new EndpointRule(local, localPort).ToString(), Remote = new EndpointRule(remote, remotePort).ToString(), Loopback = loopback, Included = included, Protocol = protocol };
                    connection.Node = connectionOrder.AddLast(key);
                }
                else { connectionOrder.Remove(connection.Node!); connectionOrder.AddLast(connection.Node!); }
                connection.LastSeen = Stopwatch.GetTimestamp();
                if (upload) { connection.Up += bytes; connection.TotalUp += bytes; } else { connection.Down += bytes; connection.TotalDown += bytes; }
            }
            if (!included) return;
            if (upload) { c.Up += bytes; totalUp += bytes; } else { c.Down += bytes; totalDown += bytes; }
        }
    }
    public Snapshot Drain(double seconds)
    {
        lock (gate)
        {
            long up = pendingUp, down = pendingDown;
            pendingUp = pendingDown = 0;
            var rows = new List<ProcessTraffic>();
            foreach (var kv in targets)
            {
                var c = kv.Value;
                up += c.Up; down += c.Down;
                if (c.Active) rows.Add(new(kv.Key, c.Name, c.Up / seconds, c.Down / seconds));
                c.Up = c.Down = 0;
            }
            foreach (var key in targets.Where(k => !k.Value.Active).Select(k => k.Key).ToArray()) targets.Remove(key);
            var cutoff = Stopwatch.GetTimestamp() - 300L * Stopwatch.Frequency;
            foreach (var key in connections.Where(pair => pair.Value.LastSeen < cutoff || !targets.ContainsKey(pair.Value.Pid)).Select(pair => pair.Key).ToArray()) RemoveConnection(key);
            var connectionRows = new List<ConnectionTraffic>();
            foreach (var connection in connections.Values)
            {
                connectionRows.Add(new(connection.Pid, connection.Name, connection.Local, connection.Remote, connection.Up / seconds, connection.Down / seconds, connection.TotalUp, connection.TotalDown, connection.Loopback, connection.Included) { Protocol = connection.Protocol });
                connection.Up = connection.Down = 0;
            }
            return new(up, down, totalUp, totalDown, seconds, rows.OrderBy(r => r.Pid).ToList()) { Connections = connectionRows };
        }
    }
    public Snapshot Sample()
    {
        lock (gate)
        {
            var timestamp = Stopwatch.GetTimestamp();
            var seconds = Math.Max(0.001, (timestamp - lastSampleTimestamp) / (double)Stopwatch.Frequency);
            lastSampleTimestamp = timestamp;
            return Drain(seconds);
        }
    }
}

public interface IMonitorCollector : IDisposable
{
    TrafficAccumulator Counters { get; }
    bool Ready { get; }
    string? Error { get; }
    long EventsLost { get; }
    void Start();
}
public sealed class NetworkCollector : IMonitorCollector
{
    private readonly object lifecycle = new();
    private TraceEventSession? session;
    private Task? worker;
    private Task? discovery;
    private readonly CancellationTokenSource discoveryStop = new();
    private volatile bool stopping;
    public TrafficAccumulator Counters { get; }
    private volatile bool ready;
    public bool Ready { get => ready; private set => ready = value; }
    public string? Error { get; private set; }
    private long lostEvents;
    public long EventsLost
    {
        get
        {
            lock (lifecycle)
            {
                // Source.EventsLost is a startup snapshot; Session.EventsLost queries live statistics.
                if (Ready && session != null) lostEvents = session.EventsLost;
                return lostEvents;
            }
        }
    }
    public NetworkCollector(Settings settings)
    { Counters = new(settings.Names, settings.IncludeLoopback, settings.ConnectionMode, settings.EndpointFilters); }
    public void Start()
    {
        var discoveryToken = discoveryStop.Token;
        discovery = Task.Run(async () =>
        {
            try
            {
                while (!discoveryToken.IsCancellationRequested)
                {
                    Counters.RefreshProcesses();
                    await Task.Delay(TimeSpan.FromSeconds(5), discoveryToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (discoveryToken.IsCancellationRequested) { }
            catch (Exception e) { if (!stopping) Error = "进程发现失败：" + e.Message; }
        });
        worker = Task.Run(() =>
        {
            try
            {
                lock (lifecycle)
                {
                    if (stopping) return;
                    session = new("FRPMonitor-" + Environment.ProcessId);
                    session.StopOnDispose = true;
                    session.BufferSizeMB = 64;
                    // Source implicitly starts a non-kernel session if accessed first.
                    // Configure the kernel provider before opening Source.
                    session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP | KernelTraceEventParser.Keywords.Process);
                    var kernel = session.Source.Kernel;
                    kernel.ProcessStart += d => Counters.StartProcess(d.ProcessID, d.ImageFileName);
                    kernel.ProcessStop += d => Counters.StopProcess(d.ProcessID);
                    kernel.ProcessDCStart += d => Counters.StartProcess(d.ProcessID, d.ImageFileName);
                    kernel.TcpIpSend += d => { if (Counters.IsTarget(d.ProcessID)) Counters.Record(d.ProcessID, d.size, true, d.saddr, d.daddr, d.sport, d.dport); };
                    kernel.TcpIpRecv += d => { if (Counters.IsTarget(d.ProcessID)) Counters.Record(d.ProcessID, d.size, false, d.saddr, d.daddr, d.sport, d.dport); };
                    kernel.TcpIpSendIPV6 += d => { if (Counters.IsTarget(d.ProcessID)) Counters.Record(d.ProcessID, d.size, true, d.saddr, d.daddr, d.sport, d.dport); };
                    kernel.TcpIpRecvIPV6 += d => { if (Counters.IsTarget(d.ProcessID)) Counters.Record(d.ProcessID, d.size, false, d.saddr, d.daddr, d.sport, d.dport); };
                    kernel.UdpIpSend += d => { if (Counters.IsTarget(d.ProcessID)) Counters.Record(d.ProcessID, d.size, true, d.saddr, d.daddr, d.sport, d.dport, "UDP"); };
                    kernel.UdpIpRecv += d => { if (Counters.IsTarget(d.ProcessID)) Counters.Record(d.ProcessID, d.size, false, d.saddr, d.daddr, d.sport, d.dport, "UDP"); };
                    kernel.UdpIpSendIPV6 += d => { if (Counters.IsTarget(d.ProcessID)) Counters.Record(d.ProcessID, d.size, true, d.saddr, d.daddr, d.sport, d.dport, "UDP"); };
                    kernel.UdpIpRecvIPV6 += d => { if (Counters.IsTarget(d.ProcessID)) Counters.Record(d.ProcessID, d.size, false, d.saddr, d.daddr, d.sport, d.dport, "UDP"); };
                    Ready = true;
                }
                session.Source.Process();
                if (!stopping) Error = "ETW 采集会话已停止，请重启采集。";
            }
            catch (Exception e) { if (!stopping) Error = "采集失败：" + e.Message; }
            finally
            {
                Ready = false;
                lock (lifecycle) { session?.Dispose(); session = null; }
            }
        });
    }
    public void Dispose()
    {
        stopping = true;
        discoveryStop.Cancel();
        lock (lifecycle) { session?.Dispose(); }
        Task.WhenAll(new[] { worker, discovery }.Where(task => task != null).Select(task => task!)).Wait(TimeSpan.FromSeconds(3));
    }
}
