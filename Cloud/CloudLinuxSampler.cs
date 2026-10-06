namespace FRPMonitor.Cloud;

internal static class CloudLinuxSampler
{
    // One persistent remote process. Reads kernel counters only; no installation or root required.
    internal const string Command = "python3 -u - <<'FRPMONITOR_SAMPLE'\n" + """
import json, time
def interfaces():
    names = []
    with open('/proc/net/route') as f:
        for line in list(f)[1:]:
            p = line.split()
            if len(p) > 3 and p[1] == '00000000' and int(p[3], 16) & 2:
                names.append(p[0])
    if not names: raise RuntimeError('No default route')
    return list(dict.fromkeys(names))
def counters(names):
    values = {}
    for name in names:
        root = '/sys/class/net/' + name + '/statistics/'
        with open(root + 'tx_bytes') as f: tx = int(f.read())
        with open(root + 'rx_bytes') as f: rx = int(f.read())
        values[name] = (tx, rx)
    return values
names = interfaces(); before = counters(names); previous = time.monotonic(); tick = previous
while True:
    tick += 1
    time.sleep(max(0.01, tick - time.monotonic()))
    current = time.monotonic(); updated_names = interfaces(); after = counters(updated_names)
    reset = names != updated_names or any(after[n][j] < before.get(n, after[n])[j] for n in updated_names for j in (0,1))
    print(json.dumps({'monotonic_ms': int(current*1000), 'seconds': current-previous, 'up_bytes': 0 if reset else sum(after[n][0]-before[n][0] for n in names), 'down_bytes': 0 if reset else sum(after[n][1]-before[n][1] for n in names), 'interfaces': ','.join(updated_names), 'reset': reset}), flush=True)
    names = updated_names; before = after; previous = current
    if current - tick > 2: tick = current
""" + "\nFRPMONITOR_SAMPLE";
}
