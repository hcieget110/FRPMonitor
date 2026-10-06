using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace FRPMonitor;

public enum ConnectionMode { All, Selected, ExternalAndSelectedLoopback }
public sealed record EndpointRule(IPAddress? Address, int Port)
{
    public bool Matches(IPAddress address, int port) => Port == port && (Address == null || Address.Equals(address));
    public override string ToString() => Address == null ? "*:" + Port : Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + Address + "]:" + Port : Address + ":" + Port;
    public static EndpointRule[] ParseAll(string input)
    {
        return input.Split(new[] { ',', ';', '，', '\r', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(value =>
        {
            var separator = value.LastIndexOf(':');
            if (separator <= 0 || !int.TryParse(value[(separator + 1)..], out var port) || port < 1 || port > 65535)
                throw new FormatException("连接填写 IP:端口，例如 127.0.0.1:7890；IPv6 使用 [::1]:7890。");
            var host = value[..separator].Trim('[', ']');
            if (host == "*") return new EndpointRule(null, port);
            if (!IPAddress.TryParse(host, out var address)) throw new FormatException("请填写 IP 地址，或使用 *:端口；可从连接明细选择实际连接。");
            return new EndpointRule(address, port);
        }).Distinct().ToArray();
    }
}
public sealed record ConnectionTraffic(int Pid, string Name, string Local, string Remote, double Upload, double Download, long TotalUp, long TotalDown, bool Loopback, bool Included)
{ public string Protocol { get; init; } = "TCP"; }
